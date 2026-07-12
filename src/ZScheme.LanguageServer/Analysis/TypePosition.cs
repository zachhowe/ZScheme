using ZScheme.Compiler.Syntax;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Detects whether a cursor position sits where a <em>type</em> is expected, so
///     completion can offer type names instead of keywords and values. Type positions
///     are annotation sites (a symbol right after <c>:</c>), anywhere inside a
///     bracketed type expression opened after <c>:</c> (e.g. <c>[x : (List I…)]</c>,
///     including nested type applications — types cannot contain expressions, so the
///     whole bracket is a type context), and the type argument of <c>new</c> /
///     <c>typeof</c>. Token-based so it works mid-edit on unbalanced source.
/// </summary>
internal static class TypePosition
{
    public static bool IsTypePosition(IReadOnlyList<Token> tokens, int line, int col)
    {
        // Bracket stack: true when the bracket opened in a type context (right after
        // ':' or as the type argument of new/typeof).
        var stack = new Stack<bool>();
        Token? prev = null;
        Token? prevPrev = null;

        foreach (var token in tokens)
        {
            if (token.Kind is TokenKind.Comment or TokenKind.Eof)
                continue;

            var startsAtOrAfterCursor =
                token.Span.Line > line || (token.Span.Line == line && token.Span.Column >= col);
            if (startsAtOrAfterCursor)
                break;

            // The partial identifier the cursor is typing inside (or at the end of) is
            // not context — the token before it is. Punctuation is never "being typed".
            var cursorTouches =
                token.Span.Line == line && col <= token.Span.Column + token.Span.Length;
            if (cursorTouches && token.Kind == TokenKind.Symbol)
                break;

            switch (token.Kind)
            {
                case TokenKind.LParen or TokenKind.LBracket:
                    stack.Push(IsTypeOpener(prev, prevPrev));
                    break;
                case TokenKind.RParen or TokenKind.RBracket:
                    if (stack.Count > 0)
                        stack.Pop();
                    break;
            }

            prevPrev = prev;
            prev = token;
        }

        if (prev?.Kind == TokenKind.Colon)
            return true;
        if (stack.Any(isType => isType))
            return true;
        return IsNewOrTypeofHead(prev, prevPrev);
    }

    private static bool IsTypeOpener(Token? prev, Token? prevPrev)
    {
        return prev?.Kind == TokenKind.Colon || IsNewOrTypeofHead(prev, prevPrev);
    }

    private static bool IsNewOrTypeofHead(Token? prev, Token? prevPrev)
    {
        return prev is { Kind: TokenKind.Symbol, Text: "new" or "typeof" }
            && prevPrev?.Kind is TokenKind.LParen or TokenKind.LBracket;
    }
}
