using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Syntax;
using ZScheme.LanguageServer.Analysis;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Handlers;

public sealed class SelectionRangeHandler(AnalysisService analysisService)
    : SelectionRangeHandlerBase
{
    protected override SelectionRangeRegistrationOptions CreateRegistrationOptions(
        SelectionRangeCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new SelectionRangeRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<Container<SelectionRange>?> Handle(
        SelectionRangeParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<Container<SelectionRange>?>(null);

        var results = request
            .Positions.Select(p => Compute(state.Source, p) ?? new SelectionRange
            {
                Range = new Range(p, p),
            })
            .ToArray();
        return Task.FromResult<Container<SelectionRange>?>(
            new Container<SelectionRange>(results)
        );
    }

    /// <summary>Expansion chain (innermost first via <c>Parent</c> links): atom token →
    ///     bracket interior (the form's contents) → bracket including delimiters → …
    ///     out to the top-level form. Purely lexical, so it works mid-edit.</summary>
    public static SelectionRange? Compute(string source, Position position)
    {
        var tokens = LexicalStructure.Tokens(source);
        var path = new List<BracketNode>();
        var scope = LexicalStructure.BuildTree(tokens);
        while (scope.FirstOrDefault(n => Contains(source, n, position)) is { } node)
        {
            path.Add(node);
            scope = node.Children;
        }

        SelectionRange? current = null;
        foreach (var node in path)
        {
            var full = new Range(TokenStart(node.Open), TokenEnd(source, node.Close));
            current = Chain(current, full, position);
            var interior = new Range(TokenEnd(source, node.Open), TokenStart(node.Close));
            current = Chain(current, interior, position);
        }

        var atom = path.LastOrDefault()?.AtomTokens
            ?? tokens.Where(t => t.Kind != TokenKind.Eof).ToList();
        var hit = atom.FirstOrDefault(t =>
            RangeContains(new Range(TokenStart(t), TokenEnd(source, t)), position)
        );
        if (hit is not null)
            current = Chain(
                current,
                new Range(TokenStart(hit), TokenEnd(source, hit)),
                position
            );

        return current;
    }

    /// <summary>Adds one step to the chain, skipping ranges that don't contain the
    ///     position (e.g. the interior when the cursor sits on a delimiter) or that
    ///     don't shrink the selection.</summary>
    private static SelectionRange? Chain(SelectionRange? parent, Range range, Position position)
    {
        if (!RangeContains(range, position))
            return parent;
        if (parent is not null && range == parent.Range)
            return parent;
        return new SelectionRange { Range = range, Parent = parent! };
    }

    private static bool Contains(string source, BracketNode node, Position position)
    {
        return RangeContains(
            new Range(TokenStart(node.Open), TokenEnd(source, node.Close)),
            position
        );
    }

    private static bool RangeContains(Range range, Position position)
    {
        return position >= range.Start && position <= range.End;
    }

    private static Position TokenStart(Token token)
    {
        return new Position(token.Span.Line - 1, token.Span.Column - 1);
    }

    /// <summary>0-based exclusive end of a token. String literals are rescanned from
    ///     the raw source because their span length is the unescaped value length (and
    ///     they may cross lines).</summary>
    private static Position TokenEnd(string source, Token token)
    {
        if (token.Kind == TokenKind.StringLit)
        {
            var start = SourceText.OffsetAt(
                source,
                token.Span.Line - 1,
                token.Span.Column - 1
            );
            var (line, character) = SourceText.PositionAt(
                source,
                LexicalStructure.StringEndOffset(source, start)
            );
            return new Position(line, character);
        }

        return new Position(token.Span.Line - 1, token.Span.Column - 1 + token.Span.Length);
    }
}
