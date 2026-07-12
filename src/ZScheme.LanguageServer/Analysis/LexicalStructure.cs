using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>A bracketed form recovered from the raw token stream. Unlike AST/SExpr
///     spans (single-line <see cref="SourceSpan" />s), the open and close tokens carry
///     the exact multi-line extent of the form.</summary>
internal sealed record BracketNode(
    Token Open,
    Token Close,
    IReadOnlyList<BracketNode> Children,
    IReadOnlyList<Token> AtomTokens
);

/// <summary>
///     Lexical (token-level) structure over a document's raw text, for features that
///     need multi-line extents or comment tokens — neither of which survive parsing:
///     <see cref="SourceSpan" /> has no end position (multi-line list spans are
///     meaningless) and the parser drops comments. Tolerant of unbalanced brackets so
///     it keeps working mid-edit.
/// </summary>
internal static class LexicalStructure
{
    /// <summary>Lexes <paramref name="source" /> keeping comment tokens; lexer
    ///     diagnostics are discarded (the analysis pipeline reports those).</summary>
    public static IReadOnlyList<Token> Tokens(string source, string file = "lsp")
    {
        return new Lexer(source, file, new DiagnosticBag()).Tokenize(keepComments: true);
    }

    /// <summary>Builds the bracket tree by paren/bracket matching over the token
    ///     stream. <c>(</c> and <c>[</c> are treated uniformly (a mismatched closer
    ///     still closes), an unclosed bracket closes at the last token, and stray
    ///     closers at the top level are skipped. Returns the top-level forms.</summary>
    public static IReadOnlyList<BracketNode> BuildTree(IReadOnlyList<Token> tokens)
    {
        var pos = 0;
        var topLevel = new List<BracketNode>();
        while (pos < tokens.Count && tokens[pos].Kind != TokenKind.Eof)
            if (tokens[pos].Kind is TokenKind.LParen or TokenKind.LBracket)
                topLevel.Add(ParseBracket(tokens, ref pos));
            else
                pos++;

        return topLevel;
    }

    private static BracketNode ParseBracket(IReadOnlyList<Token> tokens, ref int pos)
    {
        var open = tokens[pos++];
        var children = new List<BracketNode>();
        var atoms = new List<Token>();
        while (pos < tokens.Count && tokens[pos].Kind != TokenKind.Eof)
        {
            var token = tokens[pos];
            switch (token.Kind)
            {
                case TokenKind.LParen or TokenKind.LBracket:
                    children.Add(ParseBracket(tokens, ref pos));
                    break;
                case TokenKind.RParen or TokenKind.RBracket:
                    pos++;
                    return new BracketNode(open, token, children, atoms);
                default:
                    atoms.Add(token);
                    pos++;
                    break;
            }
        }

        // Unclosed at EOF: close the form at the last real token so consumers still
        // get a usable extent while the user is typing.
        var close = pos > 0 && tokens.Count > 0 ? tokens[Math.Min(pos, tokens.Count) - 1] : open;
        return new BracketNode(open, close, children, atoms);
    }

    /// <summary>
    ///     Offset just past the closing quote of the string literal whose opening quote
    ///     is at <paramref name="startOffset" />, honoring <c>\"</c> escapes and
    ///     multi-line strings. A <see cref="TokenKind.StringLit" /> token's span length
    ///     is the <em>unescaped</em> value length, so raw extents must be rescanned.
    ///     Unterminated strings end at end-of-source.
    /// </summary>
    public static int StringEndOffset(string source, int startOffset)
    {
        if (startOffset >= source.Length || source[startOffset] != '"')
            return startOffset;

        for (var i = startOffset + 1; i < source.Length; i++)
            switch (source[i])
            {
                case '\\':
                    i++;
                    break;
                case '"':
                    return i + 1;
            }

        return source.Length;
    }
}
