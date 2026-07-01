using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Syntax;

public sealed record MacroDefinition(
    string Name,
    IReadOnlyList<string> Literals,
    IReadOnlyList<MacroRule> Rules,
    SourceSpan Span
);

public sealed record MacroRule(MacroPattern Pattern, MacroTemplate Template, SourceSpan Span);

public abstract record MacroPattern(SourceSpan Span)
{
    public sealed record Literal(string Name, SourceSpan Span) : MacroPattern(Span);

    public sealed record Variable(string Name, SourceSpan Span) : MacroPattern(Span);

    public sealed record Wildcard(SourceSpan Span) : MacroPattern(Span);

    public sealed record PatList(IReadOnlyList<MacroPattern> Elements, SourceSpan Span)
        : MacroPattern(Span);

    public sealed record PatBracketList(IReadOnlyList<MacroPattern> Elements, SourceSpan Span)
        : MacroPattern(Span);

    public sealed record Ellipsis(MacroPattern Inner, SourceSpan Span) : MacroPattern(Span);
}

public abstract record MacroTemplate(SourceSpan Span)
{
    public sealed record Datum(SExpr Value, SourceSpan Span) : MacroTemplate(Span);

    public sealed record Variable(string Name, SourceSpan Span) : MacroTemplate(Span);

    public sealed record TList(IReadOnlyList<MacroTemplate> Elements, SourceSpan Span)
        : MacroTemplate(Span);

    public sealed record TBracketList(IReadOnlyList<MacroTemplate> Elements, SourceSpan Span)
        : MacroTemplate(Span);

    public sealed record Ellipsis(MacroTemplate Inner, SourceSpan Span) : MacroTemplate(Span);
}

public abstract record MacroBinding
{
    public sealed record Single(SExpr Value) : MacroBinding;

    public sealed record Repeated(IReadOnlyList<MacroBinding> Items) : MacroBinding;
}

public sealed class MacroScope(string macroName)
{
    private int _counter;

    public string Gensym(string baseName)
    {
        return $"{baseName}__{macroName}_{_counter++}";
    }
}
