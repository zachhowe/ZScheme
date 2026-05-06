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
        string source, [CallerMemberName] string testName = "")
    {
        var (svc, uri) = LspTestSession.Open(source, testName: testName);
        var handler = new CompletionHandler(svc);
        return await handler.Handle(
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
                Position = new Position(0, 0)
            },
            CancellationToken.None);
    }

    private static async Task<CompletionList> CompleteUnknownDocumentAsync()
    {
        var svc = new AnalysisService();
        var handler = new CompletionHandler(svc);
        return await handler.Handle(
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse("file:///nonexistent.zs")),
                Position = new Position(0, 0)
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Completion_AlwaysIncludesCoreKeywords()
    {
        var items = await CompleteAsync("(module test)");

        Assert.Contains(items, i => i.Label == "define" && i.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, i => i.Label == "match" && i.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, i => i.Label == "define-record" && i.Kind == CompletionItemKind.Keyword);
        Assert.Contains(items, i => i.Label == "if" && i.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public async Task Completion_AlwaysIncludesBuiltinTypes()
    {
        var items = await CompleteAsync("(module test)");

        Assert.Contains(items, i => i.Label == "Int" && i.Kind == CompletionItemKind.Class);
        Assert.Contains(items, i => i.Label == "List" && i.Kind == CompletionItemKind.Class);
        Assert.Contains(items, i => i.Label == "Result" && i.Kind == CompletionItemKind.Class);
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
    public async Task Completion_ExcludesParameters()
    {
        var src = """
            (module test)
            (define (square [some-unique-param : Int]) : Int (* some-unique-param some-unique-param))
            """;
        var items = await CompleteAsync(src);

        Assert.DoesNotContain(items, i => i.Label == "some-unique-param");
    }

    [Fact]
    public async Task Completion_UnknownDocument_StillReturnsBuiltins()
    {
        var items = await CompleteUnknownDocumentAsync();

        Assert.Contains(items, i => i.Label == "define");
        Assert.Contains(items, i => i.Label == "Int");
        Assert.Contains(items, i => i.Label == "Some");
    }
}
