using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Formatter;

public static class ImportMerger
{
    public static List<SExpr> MergeImports(List<SExpr>? sExprs)
    {
        if (sExprs == null)
            return [];

        var result = new List<SExpr>();
        var pendingImports = new List<(string Name, SourceSpan Span)>();
        // Span of the first import form in the current run, so the merged node keeps a real source
        // position for comment anchoring (rather than SourceSpan.None).
        var pendingSpan = SourceSpan.None;

        foreach (var expr in sExprs)
        {
            if (TryCollectImportNames(expr) is { Count: > 0 } names)
            {
                if (pendingImports.Count == 0)
                    pendingSpan = expr.Span;
                pendingImports.AddRange(names);
            }
            else
            {
                if (pendingImports.Count > 0)
                {
                    result.Add(CreateMergedImport(pendingImports, pendingSpan));
                    pendingImports.Clear();
                }

                result.Add(expr);
            }
        }

        if (pendingImports.Count > 0)
            result.Add(CreateMergedImport(pendingImports, pendingSpan));

        return result;
    }

    private static List<(string Name, SourceSpan Span)>? TryCollectImportNames(SExpr expr)
    {
        if (expr is not SExpr.SList list || list.Items.Count < 2)
            return null;

        if (list.Items[0] is not SExpr.Atom atom || atom.Text != "import")
            return null;

        var names = new List<(string, SourceSpan)>();
        for (var i = 1; i < list.Items.Count; i++)
            if (list.Items[i] is SExpr.Atom nameAtom)
                names.Add((nameAtom.Text, nameAtom.Span));
            else
                return null;

        return names;
    }

    private static SExpr CreateMergedImport(
        IReadOnlyList<(string Name, SourceSpan Span)> names,
        SourceSpan span
    )
    {
        var atoms = new List<SExpr> { new SExpr.Atom(new Token(TokenKind.Symbol, "import", span)) };
        atoms.AddRange(
            names.Select(n => new SExpr.Atom(new Token(TokenKind.Symbol, n.Name, n.Span)))
        );
        return new SExpr.SList(atoms, span);
    }
}
