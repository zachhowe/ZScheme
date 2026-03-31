using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Ast;

public abstract record Pattern(SourceSpan Span)
{
    public ZType? ResolvedType { get; set; }

    // _
    public sealed record Wildcard(SourceSpan Span) : Pattern(Span);

    // x (binds a variable)
    public sealed record Variable(string Name, SourceSpan Span) : Pattern(Span);

    // 42, "hello", true, etc.
    public sealed record Literal(object Value, SourceSpan Span) : Pattern(Span);

    // (Circle r) or (Rect w h) — constructor pattern
    public sealed record Constructor(
        string Name,
        IReadOnlyList<Pattern> Fields,
        SourceSpan Span) : Pattern(Span);
}
