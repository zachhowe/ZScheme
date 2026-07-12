using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Tests;

public sealed class SemanticTokensTests
{
    private const string Source = """
        (define (scale [p : Int] [f : Int]) (* p f))
        (define-record Point [x : Int])
        (define-union Shape
          (Circle [radius : Int])
          (Square [w : Int]))
        (define (dist [p : Point]) p)
        (define (area [s : Shape])
          (match s
            [(Circle r) r]
            [(Square w) w]))
        ; a comment
        (define msg "hi")
        (define pi 3.14)
        """;

    private static IReadOnlyList<SemanticTokensHandler.SemToken> Compute(string source)
    {
        var (service, uri) = LspTestSession.Open(source);
        var state = service.GetDocument(uri)!;
        return SemanticTokensHandler.ComputeTokens(state, service.Index);
    }

    private static SemanticTokensHandler.SemToken? TokenAt(
        IReadOnlyList<SemanticTokensHandler.SemToken> tokens,
        string source,
        string text,
        int occurrence = 1
    )
    {
        var (line, col) = LspTestSession.Locate(source, text, occurrence);
        return tokens.FirstOrDefault(t => t.Line == line - 1 && t.Char == col - 1);
    }

    [Fact]
    public void SpecialFormInHeadPosition_IsKeyword()
    {
        var tokens = Compute(Source);
        Assert.Equal(SemanticTokenType.Keyword, TokenAt(tokens, Source, "define ")?.Type);
        Assert.Equal(SemanticTokenType.Keyword, TokenAt(tokens, Source, "match ")?.Type);
        Assert.Equal(SemanticTokenType.Keyword, TokenAt(tokens, Source, "define-union")?.Type);
    }

    [Fact]
    public void FunctionName_IsFunctionDeclaration()
    {
        var tokens = Compute(Source);
        var scale = TokenAt(tokens, Source, "scale");
        Assert.Equal(SemanticTokenType.Function, scale?.Type);
        Assert.True(scale?.Declaration);
    }

    [Fact]
    public void ParameterUsage_IsParameter()
    {
        var tokens = Compute(Source);
        // `p` inside (* p f)
        var usage = TokenAt(tokens, Source, "p f)");
        Assert.Equal(SemanticTokenType.Parameter, usage?.Type);
    }

    [Fact]
    public void RecordNameInAnnotation_IsType()
    {
        var tokens = Compute(Source);
        Assert.Equal(SemanticTokenType.Type, TokenAt(tokens, Source, "Point", 2)?.Type);
        // The declaration occurrence is also colored via the type-name layer.
        Assert.Equal(SemanticTokenType.Type, TokenAt(tokens, Source, "Point", 1)?.Type);
    }

    [Fact]
    public void BuiltinTypeInAnnotation_IsType()
    {
        var tokens = Compute(Source);
        Assert.Equal(SemanticTokenType.Type, TokenAt(tokens, Source, "Int")?.Type);
    }

    [Fact]
    public void UnionName_IsEnum()
    {
        var tokens = Compute(Source);
        Assert.Equal(SemanticTokenType.Enum, TokenAt(tokens, Source, "Shape")?.Type);
    }

    [Fact]
    public void ConstructorPattern_IsEnumMember()
    {
        var tokens = Compute(Source);
        Assert.Equal(SemanticTokenType.EnumMember, TokenAt(tokens, Source, "Circle", 2)?.Type);
    }

    [Fact]
    public void PatternVariable_IsVariableDeclaration()
    {
        var tokens = Compute(Source);
        var pattern = TokenAt(tokens, Source, "r) r");
        Assert.Equal(SemanticTokenType.Variable, pattern?.Type);
        Assert.True(pattern?.Declaration);
    }

    [Fact]
    public void Comment_String_Number_Classified()
    {
        var tokens = Compute(Source);
        Assert.Equal(SemanticTokenType.Comment, TokenAt(tokens, Source, "; a comment")?.Type);
        Assert.Equal(SemanticTokenType.String, TokenAt(tokens, Source, "\"hi\"")?.Type);
        Assert.Equal(SemanticTokenType.Number, TokenAt(tokens, Source, "3.14")?.Type);
    }

    [Fact]
    public void Tokens_AreSortedByPosition()
    {
        var tokens = Compute(Source);
        for (var i = 1; i < tokens.Count; i++)
        {
            var ordered =
                tokens[i].Line > tokens[i - 1].Line
                || (tokens[i].Line == tokens[i - 1].Line && tokens[i].Char > tokens[i - 1].Char);
            Assert.True(ordered, $"token {i} out of order");
        }
    }

    [Fact]
    public void MultiLineString_SplitPerLine()
    {
        var source = "(define msg \"line1\nline2\")";
        var tokens = Compute(source);
        var strings = tokens.Where(t => t.Type == SemanticTokenType.String).ToList();
        Assert.Equal(2, strings.Count);
        Assert.Equal(0, strings[0].Line);
        Assert.Equal(1, strings[1].Line);
        Assert.Equal(0, strings[1].Char);
    }

    [Fact]
    public void KeywordFlag_IsKeyword()
    {
        var source = "(define f (lambda ([x : Int]) x))\n(f #:named 1)";
        var tokens = Compute(source);
        Assert.Equal(SemanticTokenType.Keyword, TokenAt(tokens, source, "#:named")?.Type);
    }

    [Fact]
    public async Task RangeRequest_ClipsToTheRequestedLines()
    {
        var src = """
            (module test)
            (define first 1)
            (define second 2)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var handler = new SemanticTokensHandler(svc);

        var full = await handler.Handle(
            new SemanticTokensParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
            },
            CancellationToken.None
        );
        var ranged = await handler.Handle(
            new SemanticTokensRangeParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
                Range = new Range(new Position(1, 0), new Position(1, 100)),
            },
            CancellationToken.None
        );

        Assert.NotNull(full);
        Assert.NotNull(ranged);
        // The ranged response covers one line of three — strictly fewer tokens.
        Assert.True(ranged!.Data.Length < full!.Data.Length);
        Assert.True(ranged.Data.Length > 0);
    }

    [Fact]
    public async Task DeltaRequest_ReusesTheCachedDocument()
    {
        var src = """
            (module test)
            (define first 1)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var handler = new SemanticTokensHandler(svc);

        var full = await handler.Handle(
            new SemanticTokensParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
            },
            CancellationToken.None
        );
        Assert.NotNull(full!.ResultId);

        var delta = await handler.Handle(
            new SemanticTokensDeltaParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.Parse(uri)),
                PreviousResultId = full.ResultId!,
            },
            CancellationToken.None
        );

        // Unchanged document + matching previousResultId → an edits-shaped response
        // (the base can only produce it when GetSemanticTokensDocument returned the
        // same cached document instance).
        Assert.NotNull(delta);
        Assert.True(delta!.IsDelta);
    }
}
