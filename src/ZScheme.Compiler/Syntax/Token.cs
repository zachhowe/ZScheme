using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Syntax;

public enum TokenKind
{
    LParen,
    RParen,
    LBracket,
    RBracket,
    Symbol,
    IntLit,
    FloatLit,
    StringLit,
    BoolLit,
    NullLit,
    Colon,
    Dot,
    Quote,
    Quasiquote,
    Unquote,
    UnquoteSplicing,
    Comment,
    Eof,
}

public sealed record Token(TokenKind Kind, string Text, SourceSpan Span)
{
    public override string ToString()
    {
        return $"{Kind}({Text})";
    }
}
