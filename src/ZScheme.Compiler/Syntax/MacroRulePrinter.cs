using System.Text;

namespace ZScheme.Compiler.Syntax;

/// <summary>
///     Renders parsed <see cref="MacroPattern" />s and <see cref="MacroTemplate" />s back to
///     s-expression text, for displaying which <c>syntax-rules</c> rule fired.
/// </summary>
public static class MacroRulePrinter
{
    public static string Print(MacroRule rule)
    {
        return $"{Print(rule.Pattern)} => {Print(rule.Template)}";
    }

    public static string Print(MacroPattern pattern)
    {
        return pattern switch
        {
            MacroPattern.Literal lit => lit.Name,
            MacroPattern.Variable v => v.Name,
            MacroPattern.Wildcard => "_",
            MacroPattern.Ellipsis e => $"{Print(e.Inner)} ...",
            MacroPattern.PatList pl => PrintElements(pl.Elements, '(', ')', Print),
            MacroPattern.PatBracketList pbl => PrintElements(pbl.Elements, '[', ']', Print),
            _ => pattern.ToString() ?? "",
        };
    }

    public static string Print(MacroTemplate template)
    {
        return template switch
        {
            MacroTemplate.Datum d => SExprPrinter.Print(d.Value, null, int.MaxValue).Text,
            MacroTemplate.Variable v => v.Name,
            MacroTemplate.Ellipsis e => $"{Print(e.Inner)} ...",
            MacroTemplate.TList tl => PrintElements(tl.Elements, '(', ')', Print),
            MacroTemplate.TBracketList bl => PrintElements(bl.Elements, '[', ']', Print),
            _ => template.ToString() ?? "",
        };
    }

    private static string PrintElements<T>(
        IReadOnlyList<T> elements,
        char open,
        char close,
        Func<T, string> print
    )
    {
        var sb = new StringBuilder();
        sb.Append(open);
        for (var i = 0; i < elements.Count; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(print(elements[i]));
        }
        sb.Append(close);
        return sb.ToString();
    }
}
