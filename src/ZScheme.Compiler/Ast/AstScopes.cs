namespace ZScheme.Compiler.Ast;

/// <summary>
///     Scope-shaped AST helpers shared by the binding analyzers
///     (<c>Types/UnusedBindingAnalyzer.cs</c>, <c>Types/TailRecursionAnalyzer.cs</c>).
///     Kept in one place because <see cref="Children" /> is an exhaustive node enumeration
///     that silently goes stale when a new <see cref="AstNode" /> kind is added — one copy
///     is one thing to update.
/// </summary>
internal static class AstScopes
{
    /// <summary>Top-level forms of a program, with <c>module</c> bodies flattened in.</summary>
    public static List<AstNode> TopLevelForms(AstNode.Program program)
    {
        var forms = new List<AstNode>();
        foreach (var form in program.TopLevelForms)
            if (form is AstNode.ModuleDecl mod)
                forms.AddRange(mod.Body);
            else
                forms.Add(form);
        return forms;
    }

    /// <summary>Whether <paramref name="pattern" /> binds <paramref name="name" />.</summary>
    public static bool PatternBinds(Pattern pattern, string name)
    {
        return pattern switch
        {
            Pattern.Variable v => v.Name == name,
            Pattern.Constructor c => c.Fields.Any(f => PatternBinds(f, name)),
            Pattern.Tuple t => t.Elements.Any(e => PatternBinds(e, name)),
            _ => false,
        };
    }

    /// <summary>Plain child enumeration (no scope logic) driving walks that must visit
    ///     every sub-expression of a node.</summary>
    public static IEnumerable<AstNode> Children(AstNode node)
    {
        return node switch
        {
            AstNode.Program p => p.TopLevelForms,
            AstNode.ModuleDecl m => m.Body,
            AstNode.Define d => [d.Body],
            AstNode.DefineAsync d => [d.Body],
            AstNode.DefineValue d => [d.Value],
            AstNode.Let l => [l.Value, l.Body],
            AstNode.Letrec lr => [.. lr.Bindings.Select(b => b.Value), lr.Body],
            AstNode.Use u => [u.Value, u.Body],
            AstNode.If i => [i.Condition, i.Then, i.Else],
            AstNode.Lambda l => [l.Body],
            AstNode.Apply a => [a.Function, .. a.Args],
            AstNode.Partial p => [p.Function, .. p.Args],
            AstNode.Match m => [m.Scrutinee, .. m.Arms.Select(a => a.Body)],
            AstNode.TupleNew t => t.Elements,
            AstNode.Raise r => [r.Expr],
            AstNode.Await a => [a.Expr],
            AstNode.ClrNew c => c.Args,
            AstNode.SuperMethodCall s => s.Args,
            AstNode.With w => [w.Record, .. w.Updates.Select(u => u.Value)],
            AstNode.WithHandlers wh => [wh.Body, .. wh.Handlers.Select(h => h.HandlerBody)],
            AstNode.SetField sf => [sf.Value],
            AstNode.ObjectExpr oe => ObjectChildren(oe.Methods, oe.Constructor),
            AstNode.ClassDecl cd => ObjectChildren(cd.Methods, cd.Constructor),
            _ => [],
        };
    }

    /// <summary>The expressions a constructor's parameters are in scope over.</summary>
    public static IEnumerable<AstNode> ConstructorScope(ConstructorDecl constructor)
    {
        foreach (var arg in constructor.SuperArgs ?? [])
            yield return arg;
        foreach (var (_, value) in constructor.FieldSets)
            yield return value;
        foreach (var expr in constructor.BodyExprs)
            yield return expr;
    }

    private static IEnumerable<AstNode> ObjectChildren(
        IReadOnlyList<ObjectMethod> methods,
        ConstructorDecl? constructor
    )
    {
        foreach (var method in methods)
            yield return method.Body;
        if (constructor is null)
            yield break;
        foreach (var node in ConstructorScope(constructor))
            yield return node;
    }
}
