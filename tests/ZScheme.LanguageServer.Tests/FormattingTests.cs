using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using TextEdit = OmniSharp.Extensions.LanguageServer.Protocol.Models.TextEdit;

namespace ZScheme.LanguageServer.Tests;

public sealed class FormattingTests
{
    private const string Source = "(module test)\n(define (f [x : Int]) : Int x)\n";

    /// <summary>A formatter whose behaviour the test controls.</summary>
    private sealed class StubFormatter(IReadOnlyList<TextEdit>? result) : ISourceFormatter
    {
        public Range? SeenRange { get; private set; }

        public IReadOnlyList<TextEdit>? Format(string source, Range? range)
        {
            SeenRange = range;
            return result;
        }
    }

    private static TextEdit OneEdit(string source) =>
        new()
        {
            Range = new Range(new Position(1, 0), new Position(1, 30)),
            NewText = "(define (f [x : Int]) : Int x)",
        };

    [Fact]
    public async Task NullFormatter_DeclinesAllThreeRequests()
    {
        var (svc, uri) = LspTestSession.Open(Source);
        var formatting = new DocumentFormattingHandler(svc, new NullFormatter());
        var rangeFormatting = new DocumentRangeFormattingHandler(svc, new NullFormatter());
        var onType = new DocumentOnTypeFormattingHandler(svc, new NullFormatter());

        var doc = new TextDocumentIdentifier(DocumentUri.Parse(uri));

        Assert.Null(
            await formatting.Handle(
                new DocumentFormattingParams { TextDocument = doc },
                CancellationToken.None
            )
        );
        var rangeResult = await rangeFormatting.Handle(
            new DocumentRangeFormattingParams
            {
                TextDocument = doc,
                Range = new Range(new Position(0, 0), new Position(1, 0)),
            },
            CancellationToken.None
        );
        // The range base models the LSP result as non-nullable, so its decline is an
        // empty edit list — the document is left untouched either way.
        Assert.NotNull(rangeResult);
        Assert.Empty(rangeResult);
        Assert.Null(
            await onType.Handle(
                new DocumentOnTypeFormattingParams
                {
                    TextDocument = doc,
                    Character = "\n",
                    Position = new Position(1, 0),
                },
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task FormatterEdits_FlowThroughUnchanged()
    {
        var (svc, uri) = LspTestSession.Open(Source);
        var edit = OneEdit(Source);
        var stub = new StubFormatter([edit]);
        var doc = new TextDocumentIdentifier(DocumentUri.Parse(uri));

        var formatting = new DocumentFormattingHandler(svc, stub);
        var result = await formatting.Handle(
            new DocumentFormattingParams { TextDocument = doc },
            CancellationToken.None
        );

        Assert.NotNull(result);
        var single = Assert.Single(result);
        Assert.Equal(edit.NewText, single.NewText);
        Assert.Null(stub.SeenRange); // full-document request passes a null range

        var rangeStub = new StubFormatter([edit]);
        var rangeResult = await new DocumentRangeFormattingHandler(svc, rangeStub).Handle(
            new DocumentRangeFormattingParams
            {
                TextDocument = doc,
                Range = new Range(new Position(1, 0), new Position(1, 30)),
            },
            CancellationToken.None
        );
        Assert.NotNull(rangeResult);
        Assert.Single(rangeResult);
        Assert.Equal(new Range(new Position(1, 0), new Position(1, 30)), rangeStub.SeenRange);

        var onTypeResult = await new DocumentOnTypeFormattingHandler(
            svc,
            new StubFormatter([edit])
        ).Handle(
            new DocumentOnTypeFormattingParams
            {
                TextDocument = doc,
                Character = ")",
                Position = new Position(1, 30),
            },
            CancellationToken.None
        );
        Assert.NotNull(onTypeResult); // first edit of many flows through
    }

    [Fact]
    public async Task UnknownDocument_Declines()
    {
        var formatting = new DocumentFormattingHandler(
            new AnalysisService(),
            new StubFormatter([OneEdit(Source)])
        );

        Assert.Null(
            await formatting.Handle(
                new DocumentFormattingParams
                {
                    TextDocument = new TextDocumentIdentifier(
                        DocumentUri.Parse("file:///nonexistent.zs")
                    ),
                },
                CancellationToken.None
            )
        );
    }
}
