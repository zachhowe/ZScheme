namespace ZScript.Compiler.Syntax;

using ZScript.Compiler.Diagnostics;

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
    Comment,
    Eof
}

public sealed record Token(TokenKind Kind, string Text, SourceSpan Span)
{
    public override string ToString() => $"{Kind}({Text})";
}
