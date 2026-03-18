namespace ZScript.Compiler.Syntax;

using ZScript.Compiler.Diagnostics;

public abstract record SExpr(SourceSpan Span)
{
    public sealed record Atom(Token Token) : SExpr(Token.Span)
    {
        public string Text => Token.Text;
        public TokenKind Kind => Token.Kind;
        public override string ToString() => Text;
    }

    public sealed record SList(IReadOnlyList<SExpr> Items, SourceSpan Span) : SExpr(Span)
    {
        public override string ToString()
        {
            var inner = string.Join(" ", Items);
            return $"({inner})";
        }
    }

    public sealed record BracketList(IReadOnlyList<SExpr> Items, SourceSpan Span) : SExpr(Span)
    {
        public override string ToString()
        {
            var inner = string.Join(" ", Items);
            return $"[{inner}]";
        }
    }
}
