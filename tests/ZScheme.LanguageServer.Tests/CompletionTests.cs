using System.Runtime.CompilerServices;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class CompletionTests
{
    private static async Task<CompletionList> CompleteAsync(
        string source,
        Position? position = null,
        [CallerMemberName] string testName = ""
    )
    {
        var (svc, uri) = LspTestSession.Open(source, testName: testName);
        var handler = new CompletionHandler(svc);
        return await handler.Handle(
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
                Position = position ?? new Position(0, 0),
            },
            CancellationToken.None
        );
    }

    /// <summary>0-based LSP position immediately after the first occurrence of
    ///     <paramref name="token" /> in <paramref name="source" />.</summary>
    private static Position After(string source, string token)
    {
        var (line, col) = LspTestSession.Locate(source, token);
        return new Position(line - 1, col - 1 + token.Length);
    }

    private static async Task<CompletionList> CompleteUnknownDocumentAsync()
    {
        var svc = new AnalysisService();
        var handler = new CompletionHandler(svc);
        return await handler.Handle(
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier(
                    DocumentUri.Parse("file:///nonexistent.zs")
                ),
                Position = new Position(0, 0),
            },
            CancellationToken.None
        );
    }

    [Fact]
    public async Task Completion_AlwaysIncludesCoreKeywords()
    {
        var items = await CompleteAsync("(module test)");

        Assert.Contains(items, i => i.Label == "define" && i.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, i => i.Label == "match" && i.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, i => i.Label == "record" && i.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, i => i.Label == "if" && i.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public async Task Completion_TypePosition_IncludesBuiltinTypes()
    {
        var src = """
            (module test)
            (define (f [a : In]) : Int a)
            """;
        var items = await CompleteAsync(src, After(src, "In"));

        Assert.Contains(items, i => i.Label == "Int" && i.Kind == CompletionItemKind.Class);
        // Keywords and value constructors are not types.
        Assert.DoesNotContain(items, i => i.Kind == CompletionItemKind.Keyword);
        Assert.DoesNotContain(items, i => i.Label == "Some");
    }

    [Fact]
    public async Task Completion_ExpressionPosition_ExcludesBuiltinTypes()
    {
        var items = await CompleteAsync("(module test)");

        Assert.DoesNotContain(items, i => i.Label == "Int");
        Assert.Contains(items, i => i.Label == "define");
    }

    [Fact]
    public async Task Completion_TypePosition_IncludesUserDefinedTypes()
    {
        var src = """
            (module test)
            (define-record Point [px : Int] [py : Int])
            (define (f [a : Po]) : Int 1)
            """;
        var (line, col) = LspTestSession.Locate(src, "Po", 2); // the annotation, not "Point"
        var items = await CompleteAsync(src, new Position(line - 1, col - 1 + 2));

        Assert.Contains(items, i => i.Label == "Point" && i.Kind == CompletionItemKind.Struct);
        Assert.DoesNotContain(items, i => i.Label == "define");
    }

    [Fact]
    public async Task Completion_AlwaysIncludesValueConstructors()
    {
        var items = await CompleteAsync("(module test)");

        Assert.Contains(items, i => i.Label == "Some" && i.Kind == CompletionItemKind.EnumMember);
        Assert.Contains(items, i => i.Label == "None" && i.Kind == CompletionItemKind.EnumMember);
        Assert.Contains(items, i => i.Label == "Ok" && i.Kind == CompletionItemKind.EnumMember);
        Assert.Contains(items, i => i.Label == "Err" && i.Kind == CompletionItemKind.EnumMember);
    }

    [Fact]
    public async Task Completion_IncludesTopLevelFunctionAsFunction()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var items = await CompleteAsync(src);

        var square = Assert.Single(items, i => i.Label == "square");
        Assert.Equal(CompletionItemKind.Function, square.Kind);
        Assert.NotNull(square.Detail);
        Assert.Contains("Int", square.Detail);
    }

    [Fact]
    public async Task Completion_IncludesRecordAsStruct()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            """;
        var items = await CompleteAsync(src);

        var point = Assert.Single(items, i => i.Label == "Point");
        Assert.Equal(CompletionItemKind.Struct, point.Kind);
    }

    [Fact]
    public async Task Completion_IncludesUnionAndCases()
    {
        var src = """
            (module test)
            (define-union Shape (Circle [r : Int]) (Square [s : Int]))
            """;
        var items = await CompleteAsync(src);

        Assert.Contains(items, i => i.Label == "Shape" && i.Kind == CompletionItemKind.Enum);
        Assert.Contains(items, i => i.Label == "Circle" && i.Kind == CompletionItemKind.EnumMember);
        Assert.Contains(items, i => i.Label == "Square" && i.Kind == CompletionItemKind.EnumMember);
    }

    [Fact]
    public async Task Completion_IncludesParameters_InsideTheirScope()
    {
        var src = """
            (module test)
            (define (square [some-unique-param : Int]) : Int (* some-unique-param some-unique-param))
            """;
        // Complete inside the function body — after the second occurrence.
        var (line, col) = LspTestSession.Locate(src, "some-unique-param", 2);
        var items = await CompleteAsync(
            src,
            new Position(line - 1, col - 1 + "some-unique-param".Length)
        );

        var param = Assert.Single(items, i => i.Label == "some-unique-param");
        Assert.Equal(CompletionItemKind.Variable, param.Kind);
    }

    [Fact]
    public async Task Completion_ExcludesOutOfScopeLocals()
    {
        var src = """
            (module test)
            (define (square [some-unique-param : Int]) : Int (* some-unique-param some-unique-param))
            (define (other) 42)
            """;
        // Complete at the top of the file — the parameter's scope hasn't opened.
        var items = await CompleteAsync(src, new Position(0, 0));

        Assert.DoesNotContain(items, i => i.Label == "some-unique-param");
        Assert.Contains(items, i => i.Label == "square");
    }

    [Fact]
    public async Task Completion_LetBinding_OnlyOfferedInItsForm()
    {
        var src = """
            (module test)
            (define (f) : Int
              (let ([local-thing 41])
                (+ local-thing 1)))
            (define (g) : Int 42)
            """;
        var (line, col) = LspTestSession.Locate(src, "local-thing", 2);
        var inScope = await CompleteAsync(
            src,
            new Position(line - 1, col - 1 + "local-thing".Length)
        );
        Assert.Contains(inScope, i => i.Label == "local-thing");

        // In the body of g, the let has closed.
        var (gLine, gCol) = LspTestSession.Locate(src, "42");
        var outOfScope = await CompleteAsync(src, new Position(gLine - 1, gCol - 1));
        Assert.DoesNotContain(outOfScope, i => i.Label == "local-thing");
    }

    [Fact]
    public async Task Completion_FiltersByPrefixBeforeCursor()
    {
        var src = """
            (module test)
            (define (my-func) 42)
            (define (other) defi)
            """;
        var items = await CompleteAsync(src, After(src, "defi"));

        Assert.Contains(items, i => i.Label == "define");
        Assert.Contains(items, i => i.Label == "define-async");
        Assert.DoesNotContain(items, i => i.Label == "match");
        Assert.DoesNotContain(items, i => i.Label == "my-func");
    }

    [Fact]
    public async Task Completion_EmptyPrefix_ReturnsKeywordsAndDocSymbols()
    {
        var src = """
            (module test)
            (define (my-func) 42)
            """;
        var items = await CompleteAsync(src, new Position(0, 0));

        Assert.Contains(items, i => i.Label == "define");
        Assert.Contains(items, i => i.Label == "my-func");
    }

    [Fact]
    public async Task Completion_IncludesCrossFileSymbolsWithModuleDetail()
    {
        using var ws = new TempPackageWorkspace(
            "widgetpkg",
            new Dictionary<string, string>
            {
                ["lib.zs"] = "(define (make-widget [n : Int]) n)\n(export make-widget)\n",
                ["main.zs"] = "(define (go) 42)\n",
            }
        );
        ws.Service.ReindexFromDisk(ws.PathOf("lib.zs"));
        ws.Open("main.zs");

        var handler = new CompletionHandler(ws.Service);
        var items = await handler.Handle(
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(ws.UriOf("main.zs"))),
                Position = new Position(0, 0),
            },
            CancellationToken.None
        );

        var widget = Assert.Single(items, i => i.Label == "make-widget");
        Assert.NotNull(widget.Detail);
        Assert.Contains("lib", widget.Detail);
        Assert.StartsWith("z", widget.SortText);
    }

    [Fact]
    public async Task Completion_DeduplicatesDocAndIndexSymbols()
    {
        using var ws = new TempPackageWorkspace(
            "duppkg",
            new Dictionary<string, string>
            {
                ["lib.zs"] = "(define (shared-fn) 1)\n",
                ["main.zs"] = "(define (shared-fn) 2)\n",
            }
        );
        ws.Service.ReindexFromDisk(ws.PathOf("lib.zs"));
        ws.Open("main.zs");

        var handler = new CompletionHandler(ws.Service);
        var items = await handler.Handle(
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(ws.UriOf("main.zs"))),
                Position = new Position(0, 0),
            },
            CancellationToken.None
        );

        Assert.Single(items, i => i.Label == "shared-fn");
    }

    [Fact]
    public async Task Completion_UnknownDocument_StillReturnsKeywords()
    {
        var items = await CompleteUnknownDocumentAsync();

        Assert.Contains(items, i => i.Label == "define");
        Assert.Contains(items, i => i.Label == "Some");
    }
}
