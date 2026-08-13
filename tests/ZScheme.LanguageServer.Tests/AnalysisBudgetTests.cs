using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

/// <summary>
///     Guards against the class of failure where a single document's analysis never
///     produces a state, which used to leave <see cref="AnalysisService.GetDocument" />
///     returning null forever — so every navigation request answered "no result"
///     instantly, for every symbol, with nothing logged.
/// </summary>
public sealed class AnalysisBudgetTests
{
    /// <summary>Comfortably above a healthy compile and below the budget, so a genuine
    ///     regression (a hang) fails the test rather than hanging the suite.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private const string Lib = """
        (module lib)
        (define (lib-double [n : Int]) : Int (* n 2))
        (export lib-double)
        """;

    private const string App = """
        (module app)
        (import bpkg/lib)
        (define (run [n : Int]) : Int (lib-double n))
        """;

    [Fact]
    public void Analysis_OfPackageImportingFile_CompletesWithinBudget()
    {
        using var ws = new TempPackageWorkspace(
            "bpkg",
            new Dictionary<string, string> { ["lib.zs"] = Lib, ["app.zs"] = App }
        );
        ws.Open("lib.zs");

        var sw = Stopwatch.StartNew();
        var state = ws.Open("app.zs");
        sw.Stop();

        Assert.True(
            sw.Elapsed < Budget,
            $"analysis took {sw.Elapsed.TotalSeconds:0.0}s, budget is {Budget.TotalSeconds:0}s"
        );
        Assert.NotNull(state.Ast);
    }

    [Fact]
    public void Analysis_OfFrameworkDependentPackage_CompletesAndResolves()
    {
        // Regression for the language-server hang: importing a package that declares a
        // shared framework used to throw FileNotFoundException out of didOpen — the
        // compiler reflected through the default load context, which the host process had
        // already populated with older copies of the same assemblies.
        if (!SharedFrameworkInstalled("Microsoft.AspNetCore.App"))
            return;

        using var ws = new TempPackageWorkspace(
            "fwpkg",
            new Dictionary<string, string>
            {
                ["lib.zs"] = Lib,
                ["app.zs"] = App.Replace("bpkg", "fwpkg"),
            },
            framework: "Microsoft.AspNetCore.App"
        );
        ws.Open("lib.zs");

        var sw = Stopwatch.StartNew();
        var state = ws.Open("app.zs");
        sw.Stop();

        Assert.True(
            sw.Elapsed < Budget,
            $"analysis took {sw.Elapsed.TotalSeconds:0.0}s, budget is {Budget.TotalSeconds:0}s"
        );
        Assert.NotNull(state.Ast);

        // And navigation actually works through it.
        var (line, col) = ws.Locate("app.zs", "lib-double");
        var span = DefinitionHandler.ResolveDefinition(state, line, col, ws.Service.Index);
        Assert.NotNull(span);
        Assert.Equal(ws.PathOf("lib.zs"), span.Value.File);
    }

    [Fact]
    public void Analysis_OfAspNetImport_Succeeds_DespiteHostAssemblyVersions()
    {
        // The exact reproducer for the language-server hang. zs-lsp ships
        // Microsoft.Extensions.DependencyInjection.Abstractions 6.0 (via OmniSharp) while
        // the aspnet package is built against 10.0; reflecting through the default load
        // context therefore threw FileNotFoundException out of didOpen, leaving the
        // document unregistered and every navigation request silently answering nothing.
        // ClrInterop now reflects in its own AssemblyLoadContext, so the host's versions
        // are irrelevant. This test runs inside the test host, which loads the very same
        // conflicting assemblies — exactly the condition that used to break.
        var repoRoot = LspTestSession.FindRepoRoot();
        if (!Directory.Exists(Path.Combine(repoRoot, "packages", "aspnet")))
            return;
        if (!SharedFrameworkInstalled("Microsoft.AspNetCore.App"))
            return;

        var path = Path.Combine(
            repoRoot,
            "tests",
            "ZScheme.LanguageServer.Tests",
            "tmp",
            "aspnet-import-probe.zs"
        );
        var uri = LspUri.Of(path);
        const string source = "(module aspnet-import-probe)\n(import aspnet/app)\n";

        var service = new AnalysisService();
        var sw = Stopwatch.StartNew();
        var state = service.AnalyzeImmediate(uri, source, 1);
        sw.Stop();

        Assert.True(
            sw.Elapsed < Budget,
            $"analysis took {sw.Elapsed.TotalSeconds:0.0}s, budget is {Budget.TotalSeconds:0}s"
        );
        Assert.NotNull(state.Ast);
        Assert.DoesNotContain(
            state.Diagnostics.Diagnostics,
            d => d.Message.Contains("Could not load file or assembly", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void GetDocument_IsRegistered_EvenWhenSourceDoesNotCompile()
    {
        // The document must exist as soon as it is opened. Handlers key off GetDocument,
        // so a missing entry is indistinguishable from "nothing to report" and silently
        // disables every feature for that file.
        using var ws = new TempPackageWorkspace(
            "cpkg",
            new Dictionary<string, string> { ["broken.zs"] = "(module broken)\n(((((\n" }
        );

        var state = ws.Open("broken.zs");

        Assert.NotNull(state);
        Assert.NotNull(ws.Service.GetDocument(ws.UriOf("broken.zs")));
    }

    [Fact]
    public void AnalysisBudget_IsWellUnderEditorRequestTimeouts()
    {
        // Editors cancel long-running LSP requests (Zed at 120s). The budget exists so we
        // answer with a stale-but-real state first. A navigation request can wait out the
        // budget and then the pending-analysis window, so it is their sum that has to fit.
        Assert.True(AnalysisService.DefaultAnalysisBudget > TimeSpan.Zero);
        Assert.True(
            AnalysisService.DefaultAnalysisBudget + AnalysisService.DefaultPendingAnalysisWait
                < TimeSpan.FromSeconds(120)
        );
    }

    [Fact]
    public void Navigation_WaitsForAnAnalysisThatOverranItsBudget()
    {
        // The regression: an analysis that misses its budget stores a state with diagnostics
        // but no AST, and every navigation handler reads that as "this name has no
        // definition". The client cannot tell the two apart, so a document that is merely
        // still compiling looks like one where go-to-definition, hover and find-references
        // are all simply empty.
        using var ws = OverrunningWorkspace("epkg");

        var overran = ws.Open("app.zs");
        Assert.Null(overran.Ast);
        Assert.True(overran.Diagnostics.HasErrors, "the overrun should be reported, not hidden");

        // ...but a request against it still gets a real answer, because the analysis it is
        // waiting on is the one that produces the AST.
        var state = ws.Service.GetDocument(ws.UriOf("app.zs"));
        Assert.NotNull(state);
        Assert.NotNull(state.Ast);

        var (line, col) = ws.Locate("app.zs", "lib-double");
        var span = DefinitionHandler.ResolveDefinition(state, line, col, ws.Service.Index);
        Assert.NotNull(span);
        Assert.Equal(ws.PathOf("lib.zs"), span.Value.File);
    }

    [Fact]
    public void FailedReanalysis_KeepsTheLastGoodAst()
    {
        // A compile that crashes or overruns is a reason to publish a diagnostic saying so.
        // It is never a reason to drop navigation the document already had — that turns one
        // slow keystroke into a file with no hover, no go-to and no references.
        using var ws = OverrunningWorkspace("fpkg");
        ws.Open("app.zs");
        Assert.NotNull(ws.Service.GetDocument(ws.UriOf("app.zs"))?.Ast);

        // The AST the request just served has to be the document's stored state, not a private
        // answer to that one request. Until GetDocument adopted the result it read off the task,
        // whether the next analysis saw it depended on whether the publishing continuation had
        // been scheduled yet -- so this assertion, and the stale.Ast one below, were a coin flip.
        Assert.NotNull(ws.Service.PeekDocument(ws.UriOf("app.zs"))?.Ast);

        // Re-analysing perfectly good source, with a budget nothing can meet.
        var edited = App.Replace("bpkg", "fpkg") + "\n";
        var stale = ws.Service.AnalyzeImmediate(ws.UriOf("app.zs"), edited, 2);

        Assert.NotNull(stale.Ast);
        Assert.True(stale.Diagnostics.HasErrors, "the reason for the stale AST should be shown");
        Assert.Equal(edited, stale.Source);
        Assert.Equal(2, stale.Version);
    }

    [Fact]
    public void ClosedDocument_AnswersAtOnce_RatherThanWaitingOnItsAbandonedAnalysis()
    {
        // Waiting for a pending analysis must not outlive the document: once the client has
        // closed the buffer there is nothing to answer with and nothing to wait for, and a
        // late result must not put the document back.
        using var ws = OverrunningWorkspace("gpkg");
        var uri = ws.UriOf("app.zs");
        ws.Open("app.zs");
        ws.Service.RemoveDocument(uri);

        var sw = Stopwatch.StartNew();
        var state = ws.Service.GetDocument(uri);
        sw.Stop();

        Assert.Null(state);
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(5),
            $"a closed document waited {sw.Elapsed.TotalSeconds:0.0}s on an analysis nobody wants"
        );
    }

    /// <summary>A workspace whose analyses always overrun their budget (see
    ///     <c>analysisBudget</c>), with <c>lib.zs</c> already opened and indexed — the
    ///     indexing happens in the overrunning analysis, so it has to be waited out before
    ///     a cross-file lookup means anything.</summary>
    private static TempPackageWorkspace OverrunningWorkspace(string prefix)
    {
        var ws = new TempPackageWorkspace(
            prefix,
            new Dictionary<string, string>
            {
                ["lib.zs"] = Lib,
                ["app.zs"] = App.Replace("bpkg", prefix),
            },
            analysisBudget: TimeSpan.Zero
        );
        ws.Open("lib.zs");
        ws.Service.GetDocument(ws.UriOf("lib.zs"));
        return ws;
    }

    private static bool SharedFrameworkInstalled(string id)
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(dotnetRoot))
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        }

        return Directory.Exists(Path.Combine(dotnetRoot, "shared", id));
    }
}
