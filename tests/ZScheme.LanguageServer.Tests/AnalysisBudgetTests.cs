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
            new Dictionary<string, string> { ["lib.zs"] = Lib, ["app.zs"] = App.Replace("bpkg", "fwpkg") },
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
        var uri = new Uri(path).AbsoluteUri;
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
        // answer with a stale-but-real state first.
        Assert.True(AnalysisService.AnalysisBudget < TimeSpan.FromSeconds(120));
        Assert.True(AnalysisService.AnalysisBudget > TimeSpan.Zero);
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
