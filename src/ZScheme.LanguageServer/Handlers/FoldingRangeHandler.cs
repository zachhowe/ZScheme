using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Syntax;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

public sealed class FoldingRangeHandler(AnalysisService analysisService) : FoldingRangeHandlerBase
{
    protected override FoldingRangeRegistrationOptions CreateRegistrationOptions(
        FoldingRangeCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new FoldingRangeRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<Container<FoldingRange>?> Handle(
        FoldingRangeRequestParam request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<Container<FoldingRange>?>(null);

        return Task.FromResult<Container<FoldingRange>?>(
            new Container<FoldingRange>(Compute(state.Source))
        );
    }

    /// <summary>Folding ranges are purely lexical (token-level bracket matching), so
    ///     they keep working while the document has parse or type errors.</summary>
    public static IReadOnlyList<FoldingRange> Compute(string source)
    {
        var tokens = LexicalStructure.Tokens(source);
        var ranges = new List<FoldingRange>();
        foreach (var form in LexicalStructure.BuildTree(tokens))
            AddBracketRanges(form, ranges);
        AddCommentRanges(tokens, ranges);
        return ranges;
    }

    private static void AddBracketRanges(BracketNode node, List<FoldingRange> ranges)
    {
        if (node.Close.Span.Line > node.Open.Span.Line)
            ranges.Add(
                new FoldingRange
                {
                    // Keep both delimiters visible: fold starts after the opener and
                    // ends before the closer.
                    StartLine = node.Open.Span.Line - 1,
                    StartCharacter = node.Open.Span.Column,
                    EndLine = node.Close.Span.Line - 1,
                    EndCharacter = node.Close.Span.Column - 1,
                    Kind = FoldingRangeKind.Region,
                }
            );

        foreach (var child in node.Children)
            AddBracketRanges(child, ranges);
    }

    private static void AddCommentRanges(IReadOnlyList<Token> tokens, List<FoldingRange> ranges)
    {
        // A comment block is a run of >= 2 line-leading comments on consecutive lines.
        int? runStart = null;
        var runEnd = 0;
        var previousTokenLine = 0;

        void Flush()
        {
            if (runStart is { } start && runEnd > start)
                ranges.Add(
                    new FoldingRange
                    {
                        StartLine = start - 1,
                        EndLine = runEnd - 1,
                        Kind = FoldingRangeKind.Comment,
                    }
                );
            runStart = null;
        }

        foreach (var token in tokens)
        {
            var lineLeadingComment =
                token.Kind == TokenKind.Comment && token.Span.Line != previousTokenLine;
            if (lineLeadingComment && runStart is not null && token.Span.Line == runEnd + 1)
            {
                runEnd = token.Span.Line;
            }
            else if (lineLeadingComment)
            {
                Flush();
                runStart = token.Span.Line;
                runEnd = token.Span.Line;
            }
            else if (token.Kind != TokenKind.Eof)
            {
                Flush();
            }

            previousTokenLine = token.Span.Line;
        }

        Flush();
    }
}
