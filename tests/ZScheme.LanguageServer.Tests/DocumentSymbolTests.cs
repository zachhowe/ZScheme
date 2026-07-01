using System.Runtime.CompilerServices;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;
using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

namespace ZScheme.LanguageServer.Tests;

public sealed class DocumentSymbolTests
{
    private static async Task<DocumentSymbol[]> RequestAsync(
        string source,
        [CallerMemberName] string testName = ""
    )
    {
        var (svc, uri) = LspTestSession.Open(source, testName: testName);
        var handler = new DocumentSymbolHandler(svc);
        var container = await handler.Handle(
            new DocumentSymbolParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
            },
            CancellationToken.None
        );

        Assert.NotNull(container);
        return container!.Select(item => item.DocumentSymbol!).ToArray();
    }

    [Fact]
    public async Task DocumentSymbol_EmitsTopLevelFunction()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var symbols = await RequestAsync(src);

        var sq = Assert.Single(symbols, s => s.Name == "square");
        Assert.Equal(LspSymbolKind.Function, sq.Kind);
        Assert.NotNull(sq.Detail);
        Assert.Contains("Int", sq.Detail);
    }

    [Fact]
    public async Task DocumentSymbol_EmitsRecordAsStruct()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            """;
        var symbols = await RequestAsync(src);

        var p = Assert.Single(symbols, s => s.Name == "Point");
        Assert.Equal(LspSymbolKind.Struct, p.Kind);
    }

    [Fact]
    public async Task DocumentSymbol_EmitsUnionAndCases()
    {
        var src = """
            (module test)
            (define-union Shape (Circle [r : Int]) (Square [s : Int]))
            """;
        var symbols = await RequestAsync(src);

        Assert.Contains(symbols, s => s.Name == "Shape" && s.Kind == LspSymbolKind.Enum);
        Assert.Contains(symbols, s => s.Name == "Circle" && s.Kind == LspSymbolKind.EnumMember);
        Assert.Contains(symbols, s => s.Name == "Square" && s.Kind == LspSymbolKind.EnumMember);
    }

    [Fact]
    public async Task DocumentSymbol_EmitsModule()
    {
        var src = """
            (module my-mod)
            (define answer 42)
            """;
        var symbols = await RequestAsync(src);

        Assert.Contains(symbols, s => s.Name == "my-mod" && s.Kind == LspSymbolKind.Module);
    }

    [Fact]
    public async Task DocumentSymbol_FiltersOutParametersAndVariables()
    {
        var src = """
            (module test)
            (define answer 42)
            (define (square [x : Int]) : Int (* x x))
            """;
        var symbols = await RequestAsync(src);

        // "answer" is a Variable, "x" is a Parameter — both filtered.
        Assert.DoesNotContain(symbols, s => s.Name == "answer");
        Assert.DoesNotContain(symbols, s => s.Name == "x");
    }

    [Fact]
    public async Task DocumentSymbol_RangeMatchesDefinitionSpan()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var symbols = await RequestAsync(src);

        var sq = Assert.Single(symbols, s => s.Name == "square");
        // Range/SelectionRange are equal in this handler (both = DefinitionSpan).
        Assert.Equal(sq.Range, sq.SelectionRange);
        // Definition is on line 2 (0-based: 1).
        Assert.Equal(1, sq.Range.Start.Line);
    }

    [Fact]
    public async Task DocumentSymbol_UnknownDocument_ReturnsNull()
    {
        var svc = new AnalysisService();
        var handler = new DocumentSymbolHandler(svc);
        var result = await handler.Handle(
            new DocumentSymbolParams
            {
                TextDocument = new TextDocumentIdentifier(
                    DocumentUri.Parse("file:///nonexistent.zs")
                ),
            },
            CancellationToken.None
        );

        Assert.Null(result);
    }
}
