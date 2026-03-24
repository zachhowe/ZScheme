using ZScript.Compiler.Diagnostics;

namespace ZScript.Compiler.Syntax;

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
    Colon,
    Dot,
    Quote,
    Quasiquote,
    Unquote,
    UnquoteSplicing,
    Comment,
    Eof
}

public sealed record Token(TokenKind Kind, string Text, SourceSpan Span)
{
    public override string ToString()
    {
        return $"{Kind}({Text})";
    }
}
