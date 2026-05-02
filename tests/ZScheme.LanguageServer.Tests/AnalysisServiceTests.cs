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
        var (svc, uri) = LspTestSession.Open(manifest, extension: ".zspkg");
        var state = svc.GetDocument(uri);

        Assert.NotNull(state);
        // Manifest analysis does not produce a typed program.
        Assert.Null(state!.Ast);
        // A well-formed manifest has no errors.
        Assert.False(state.Diagnostics.HasErrors,
            string.Join("\n", state.Diagnostics.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void AnalyzeImmediate_MalformedZspkg_SurfacesDiagnostics()
    {
        var malformed = "(package (this-is-not-valid";
        var (svc, uri) = LspTestSession.Open(malformed, extension: ".zspkg");
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

        svc.AnalyzeImmediate(uri, brokenSrc, version: 2);
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
    public async Task AnalyzeAsync_SecondCallCancelsFirst()
    {
        var srcA = "(module a)";
        var srcB = """
            (module b)
            (define (square [x : Int]) : Int (* x x))
            """;
        var uri = LspTestSession.SyntheticUri(nameof(AnalyzeAsync_SecondCallCancelsFirst));
        var svc = new AnalysisService();

        // Fire two analyses back-to-back. The first should be cancelled in its 300ms
        // debounce window; the second should win.
        var first = svc.AnalyzeAsync(uri, srcA, version: 1);
        await Task.Delay(50);
        var second = svc.AnalyzeAsync(uri, srcB, version: 2);

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
}
