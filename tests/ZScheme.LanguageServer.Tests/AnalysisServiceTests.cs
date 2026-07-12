using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class AnalysisServiceTests
{
    [Fact]
    public void GetDocument_UnknownUri_ReturnsNull()
    {
        var svc = new AnalysisService();
        Assert.Null(svc.GetDocument("file:///never-analyzed.zs"));
    }

    [Fact]
    public void AnalyzeImmediate_StoresDocumentWithTypedAstAndSymbols()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = LspTestSession.Open(src);

        var state = svc.GetDocument(uri);

        Assert.NotNull(state);
        Assert.Equal(uri, state!.Uri);
        Assert.Equal(1, state.Version);
        Assert.NotNull(state.Ast);
        Assert.Contains(state.Symbols, s => s.Name == "square" && s.Kind == SymbolKind.Function);
        Assert.True(state.NameToDefinition.ContainsKey("square"));
    }

    [Fact]
    public void RemoveDocument_RemovesFromStore()
    {
        var src = "(module test)";
        var (svc, uri) = LspTestSession.Open(src);
        Assert.NotNull(svc.GetDocument(uri));

        svc.RemoveDocument(uri);

        Assert.Null(svc.GetDocument(uri));
    }

    [Fact]
    public void RemoveDocument_UnknownUri_DoesNotThrow()
    {
        var svc = new AnalysisService();
        svc.RemoveDocument("file:///never-existed.zs");
        // No assertion needed — just confirming this is a no-op.
    }

    [Fact]
    public void AnalyzeImmediate_ZspkgFile_RoutesThroughManifestParser()
    {
        var manifest = """
            (package
              (name "demo")
              (version "0.1.0")
              (import-prefix "demo"))
            """;
        var (svc, uri) = LspTestSession.Open(manifest, ".zspkg");
        var state = svc.GetDocument(uri);

        Assert.NotNull(state);
        // Manifest analysis does not produce a typed program.
        Assert.Null(state!.Ast);
        // A well-formed manifest has no errors.
        Assert.False(
            state.Diagnostics.HasErrors,
            string.Join("\n", state.Diagnostics.Diagnostics.Select(d => d.Message))
        );
    }

    [Fact]
    public void AnalyzeImmediate_MalformedZspkg_SurfacesDiagnostics()
    {
        var malformed = "(package (this-is-not-valid";
        var (svc, uri) = LspTestSession.Open(malformed, ".zspkg");
        var state = svc.GetDocument(uri);

        Assert.NotNull(state);
        Assert.True(state!.Diagnostics.HasErrors);
    }

    [Fact]
    public void AnalyzeImmediate_LastGoodFallback_PreservesSymbolsOnTransientParseError()
    {
        var goodSrc = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var brokenSrc = """
            (module test)
            (define (square [x : Int]) : Int (* x x
            """;

        var (svc, uri) = LspTestSession.Open(goodSrc);
        var goodState = svc.GetDocument(uri)!;
        var goodSymbols = goodState.Symbols;
        var goodNameToDef = goodState.NameToDefinition;

        svc.AnalyzeImmediate(uri, brokenSrc, 2);
        var fallbackState = svc.GetDocument(uri)!;

        // AST + symbol table preserved from last-good run.
        Assert.NotNull(fallbackState.Ast);
        Assert.Same(goodState.Ast, fallbackState.Ast);
        Assert.Same(goodSymbols, fallbackState.Symbols);
        Assert.Same(goodNameToDef, fallbackState.NameToDefinition);
        // Fresh diagnostics surface on the new state.
        Assert.True(fallbackState.Diagnostics.HasErrors);
        // Source + version reflect the latest input.
        Assert.Equal(brokenSrc, fallbackState.Source);
        Assert.Equal(2, fallbackState.Version);
    }

    [Fact]
    public void AnalyzeImmediate_FileInPackageTestDir_ResolvesTestDependencies()
    {
        // Reproduces the LSP regression where opening a file under a package's test
        // directory reported "Module not found: 'zunit'", because DiscoverPackages
        // ignored TestDependencies and default-module aliases. We point the LSP at a
        // synthetic file inside packages/stdlib/test/ — the same context the editor
        // sees — and verify that (import zunit) resolves.
        var repoRoot = LspTestSession.FindRepoRoot();
        var syntheticPath = Path.Combine(
            repoRoot,
            "packages",
            "stdlib",
            "test",
            "lsp_resolve_check.zs"
        );
        var uri = new Uri(syntheticPath).AbsoluteUri;

        var src = """
            (module lsp-resolve-check)
            (import zunit)
            """;

        var svc = new AnalysisService();
        var state = svc.AnalyzeImmediate(uri, src, 1);

        var moduleErrors = state
            .Diagnostics.Diagnostics.Where(d =>
                d.Message.Contains("Module not found", StringComparison.Ordinal)
            )
            .Select(d => d.Message)
            .ToList();

        Assert.Empty(moduleErrors);
    }

    [Fact]
    public void AnalyzeImmediate_FileUsingFrameworkFromHint_ResolvesSharedFrameworkAssembly()
    {
        // Regression: opening packages/aspnet/src/app.zs reported "CLR assembly not found
        // for ':from' hint: 'Microsoft.Extensions.Hosting.Abstractions'" because
        // DiscoverPackages resolved ZScheme and NuGet dependencies but ignored the
        // manifest's (framework Microsoft.AspNetCore.App) declaration. The shared-framework
        // assembly directory therefore never reached AssemblySearchPaths, so import-clr
        // :from hints pointing at framework assemblies failed only in the editor. Verify the
        // LSP now resolves declared frameworks the same way PackageTester does for real builds.
        var repoRoot = LspTestSession.FindRepoRoot();
        var path = Path.Combine(repoRoot, "packages", "aspnet", "src", "app.zs");
        var src = File.ReadAllText(path);
        var uri = new Uri(path).AbsoluteUri;

        var svc = new AnalysisService();
        var state = svc.AnalyzeImmediate(uri, src, 1);

        var assemblyErrors = state
            .Diagnostics.Diagnostics.Where(d =>
                d.Message.Contains("CLR assembly not found", StringComparison.Ordinal)
            )
            .Select(d => d.Message)
            .ToList();

        Assert.Empty(assemblyErrors);
    }

    [Theory]
    [InlineData("list.zs")]
    [InlineData("vector.zs")]
    public void AnalyzeImmediate_StdlibPreludeModule_NoAmbiguousOverload(string fileName)
    {
        // Regression: editing a stdlib module like list.zs or vector.zs in the LSP used to
        // report "Ambiguous overload of 'list'; candidates: stdlib/list/list, list/list"
        // because the file was compiled both under its bare (module ...) name and again
        // under its package-qualified name as a prelude self-import. Verify the LSP now
        // sets PrimaryModuleName so this duplicate registration does not occur.
        var repoRoot = LspTestSession.FindRepoRoot();
        var path = Path.Combine(repoRoot, "packages", "stdlib", "src", fileName);
        var src = File.ReadAllText(path);
        var uri = new Uri(path).AbsoluteUri;

        var svc = new AnalysisService();
        var state = svc.AnalyzeImmediate(uri, src, 1);

        var offending = state
            .Diagnostics.Diagnostics.Where(d =>
                d.Message.Contains("Ambiguous overload", StringComparison.Ordinal)
                || d.Message.Contains("Cannot use overloaded name", StringComparison.Ordinal)
            )
            .Select(d => d.Message)
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void AnalyzeImmediate_FileInPackageMainDir_RegistersUnderQualifiedName()
    {
        // Regression: ensures the LSP threads PrimaryModuleName through for files under a
        // package's main source dir. The qualified module name should be "stdlib/<file>",
        // matching what LibraryCompiler uses when compiling the package directly.
        var repoRoot = LspTestSession.FindRepoRoot();
        var path = Path.Combine(repoRoot, "packages", "stdlib", "src", "list.zs");
        var src = File.ReadAllText(path);
        var uri = new Uri(path).AbsoluteUri;

        var svc = new AnalysisService();
        var state = svc.AnalyzeImmediate(uri, src, 1);

        Assert.NotNull(state.Ast);
        // Under the bug, the diagnostics referenced both "stdlib/list/list" AND
        // "list/list". After the fix only the qualified candidate exists. We assert the
        // bare-prefix candidate name does not show up anywhere in the diagnostics.
        Assert.DoesNotContain(
            state.Diagnostics.Diagnostics,
            d =>
                d.Message.Contains("list/list", StringComparison.Ordinal)
                && !d.Message.Contains("stdlib/list/list", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task AnalyzeAsync_SecondCallCancelsFirst()
    {
        var srcA = "(module a)";
        var srcB = """
            (module b)
            (define (square [x : Int]) : Int (* x x))
            """;
        var uri = LspTestSession.SyntheticUri(nameof(AnalyzeAsync_SecondCallCancelsFirst));
        var svc = new AnalysisService();

        // Fire two analyses back-to-back. The first should be canceled in its 300ms
        // debounce window; the second should win.
        var first = svc.AnalyzeAsync(uri, srcA, 1);
        await Task.Delay(50);
        var second = svc.AnalyzeAsync(uri, srcB, 2);

        var firstResult = await first;
        var secondResult = await second;

        // First call returns the placeholder (no document state existed yet, so it
        // gets a freshly-constructed empty state with version 1).
        Assert.Null(firstResult.Ast);
        // Second call ran the full pipeline.
        Assert.NotNull(secondResult.Ast);
        Assert.Equal(2, secondResult.Version);
        // The stored document is the second result.
        Assert.Same(secondResult, svc.GetDocument(uri));
    }

    [Fact]
    public async Task ScanAdditionalRoots_IndexesFilesUnderTheNewRoot()
    {
        using var ws = new TestFixtures.TempPackageWorkspace(
            "addpkg",
            new Dictionary<string, string>
            {
                ["lib.zs"] = "(module lib)\n(define (added-fn) 1)\n(export added-fn)\n",
            }
        );

        Assert.False(ws.Service.Index.Contains(ws.PathOf("lib.zs")));

        await ws.Service.ScanAdditionalRootsAsync([ws.Root]);

        Assert.True(ws.Service.Index.Contains(ws.PathOf("lib.zs")));
        Assert.NotNull(ws.Service.Index.DefinitionInFile(ws.PathOf("lib.zs"), "added-fn"));
    }

    [Fact]
    public async Task PurgeRoot_RemovesExactlyTheSubtree()
    {
        using var inside = new TestFixtures.TempPackageWorkspace(
            "inpkg",
            new Dictionary<string, string> { ["a.zs"] = "(module a)\n(define (fn-a) 1)\n" }
        );
        using var outside = new TestFixtures.TempPackageWorkspace(
            "outpkg",
            new Dictionary<string, string> { ["b.zs"] = "(module b)\n(define (fn-b) 1)\n" }
        );
        // Index both trees into ONE service.
        await inside.Service.ScanAdditionalRootsAsync([inside.Root, outside.Root]);
        Assert.True(inside.Service.Index.Contains(inside.PathOf("a.zs")));
        Assert.True(inside.Service.Index.Contains(outside.PathOf("b.zs")));

        inside.Service.PurgeRoot(inside.Root);

        Assert.False(inside.Service.Index.Contains(inside.PathOf("a.zs")));
        Assert.True(inside.Service.Index.Contains(outside.PathOf("b.zs")));
    }
}
