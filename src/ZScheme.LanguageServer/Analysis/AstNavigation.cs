using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Shared read-only navigation over the typed AST: child enumeration, cursor
///     hit-testing, and a flat walk of every <see cref="AstNode.Name" /> occurrence.
///     Centralized here so hover, go-to-definition, and the reference collector all
///     agree on what an AST node's children are (and stay in sync when new node kinds
///     are added). The child enumeration synthesizes precise <see cref="AstNode.Name" />
///     nodes for define-names and parameters so the cursor can land on them on
///     multi-line forms.
/// </summary>
internal static class AstNavigation
{
    /// <summary>
    ///     Finds the most specific (deepest) node whose single-line span contains the
    ///     1-based (line, col) position, or null if none.
    /// </summary>
    public static AstNode? FindNodeAt(AstNode node, int line, int col)
    {
        AstNode? best = null;

        if (SpanContains(node.Span, line, col))
            best = node;

        foreach (var child in Children(node))
        {
            var found = FindNodeAt(child, line, col);
            if (found is not null)
                best = found;
        }

        return best;
    }

    /// <summary>
    ///     Finds the innermost <see cref="AstNode.Apply" /> whose subtree contains the
    ///     1-based (line, col) position, or null if the cursor is not inside any call.
    ///     Unlike <see cref="FindNodeAt" />, this walks the ancestor chain: an
    ///     <see cref="AstNode.Apply" />'s own span is single-line and won't contain a
    ///     cursor typing on a later line of a multi-line call, so we recover it from the
    ///     path to the deepest matching node. Used by signature help.
    /// </summary>
    public static AstNode.Apply? FindEnclosingApply(AstNode root, int line, int col)
    {
        var path = new List<AstNode>();
        if (!FindPath(root, line, col, path))
            return null;
        for (var i = path.Count - 1; i >= 0; i--)
            if (path[i] is AstNode.Apply apply)
                return apply;
        return null;
    }

    /// <summary>The ancestor chain (root first, deepest node last) to the deepest node
    ///     containing the 1-based (line, col) position, or null when no node contains
    ///     it. Used by <see cref="ScopeAnalysis" /> to find enclosing binders.</summary>
    public static IReadOnlyList<AstNode>? PathTo(AstNode root, int line, int col)
    {
        var path = new List<AstNode>();
        return FindPath(root, line, col, path) ? path : null;
    }

    // Records the ancestor chain to the deepest node containing the cursor. Mirrors
    // FindNodeAt's "last matching child wins" so both agree on which node is deepest.
    private static bool FindPath(AstNode node, int line, int col, List<AstNode> path)
    {
        List<AstNode>? bestChild = null;
        foreach (var child in Children(node))
        {
            var childPath = new List<AstNode>();
            if (FindPath(child, line, col, childPath))
                bestChild = childPath;
        }

        if (bestChild is not null)
        {
            path.Add(node);
            path.AddRange(bestChild);
            return true;
        }

        if (SpanContains(node.Span, line, col))
        {
            path.Add(node);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Yields every <see cref="AstNode.Name" /> occurrence reachable from
    ///     <paramref name="node" />, including the synthesized name nodes for
    ///     definitions and parameters. Used to build the workspace reference index.
    /// </summary>
    public static IEnumerable<AstNode.Name> AllNames(AstNode node)
    {
        if (node is AstNode.Name name)
            yield return name;

        foreach (var child in Children(node))
        foreach (var found in AllNames(child))
            yield return found;
    }

    private static bool SpanContains(SourceSpan span, int line, int col)
    {
        return span.Line == line && col >= span.Column && col < span.Column + span.Length;
    }

    public static IEnumerable<AstNode> Children(AstNode node)
    {
        return node switch
        {
            AstNode.Program p => p.TopLevelForms,
            AstNode.Define d => DefineNameNode(d.FnName, d.NameSpan, d.ResolvedType)
                .Concat(ParamNames(d.Params))
                .Append(d.Body),
            AstNode.DefineAsync d => DefineNameNode(d.FnName, d.NameSpan, d.ResolvedType)
                .Concat(ParamNames(d.Params))
                .Append(d.Body),
            AstNode.DefineValue d => DefineNameNode(d.VarName, d.NameSpan, d.ResolvedType)
                .Append(d.Value),
            AstNode.Let l => [l.Value, l.Body],
            AstNode.Use u => [u.Value, u.Body],
            AstNode.If i => [i.Condition, i.Then, i.Else],
            AstNode.Lambda l => ParamNames(l.Params).Append(l.Body),
            AstNode.Apply a => new[] { a.Function }.Concat(a.Args),
            AstNode.Match m => new[] { m.Scrutinee }.Concat(m.Arms.Select(a => a.Body)),
            AstNode.ModuleDecl m => m.Body,
            AstNode.Raise r => [r.Expr],
            AstNode.Await a => [a.Expr],
            AstNode.Partial p => new[] { p.Function }.Concat(p.Args),
            AstNode.With w => new[] { w.Record }.Concat(w.Updates.Select(u => u.Value)),
            AstNode.WithHandlers wh => new[] { wh.Body }.Concat(
                wh.Handlers.Select(h => h.HandlerBody)
            ),
            AstNode.SetField sf => [sf.Value],
            AstNode.TupleNew tn => tn.Elements,
            AstNode.ClrNew cn => cn.Args,
            AstNode.SuperMethodCall smc => smc.Args,
            AstNode.ObjectExpr oe => ObjectExprChildren(oe),
            AstNode.ClassDecl cd => DefineNameNode(cd.ClassName, cd.NameSpan, cd.ResolvedType)
                .Concat(ClassDeclChildren(cd)),
            AstNode.TypeAliasDecl ta => DefineNameNode(ta.AliasName, ta.NameSpan, null),
            AstNode.RecordDecl rd => DefineNameNode(rd.RecordName, rd.NameSpan, rd.ResolvedType),
            AstNode.UnionDecl ud => UnionDeclChildren(ud),
            AstNode.InterfaceDecl ifd => DefineNameNode(
                ifd.InterfaceName,
                ifd.NameSpan,
                ifd.ResolvedType
            ),
            AstNode.ImportClr ic => ic.Imports.SelectMany(i =>
                DefineNameNode(i.Alias, i.AliasSpan, i.TypeAnnotation)
            ),
            _ => [],
        };
    }

    private static IEnumerable<AstNode> UnionDeclChildren(AstNode.UnionDecl ud)
    {
        // The union name plus each case name — case names are value constructors, so
        // the cursor landing on one should resolve like any other name.
        var children = DefineNameNode(ud.UnionName, ud.NameSpan, ud.ResolvedType).ToList();
        foreach (var c in ud.Cases)
            children.AddRange(DefineNameNode(c.Name, c.NameSpan, null));
        return children;
    }

    private static IEnumerable<AstNode> ParamNames(IReadOnlyList<Param> params_)
    {
        // Synthesize a Name node per parameter so the walker can resolve the cursor to
        // a parameter binding. Carry the inferred type so hover can format it. The name
        // span is preferred over the param span (which covers the whole [name : Type]
        // bracket) so rename edits touch only the name.
        return params_.Select(p =>
            (AstNode)new AstNode.Name(p.Name, p.NameSpan.Length > 0 ? p.NameSpan : p.Span)
            {
                ResolvedType = p.ResolvedType,
            }
        );
    }

    private static IEnumerable<AstNode> DefineNameNode(
        string name,
        SourceSpan nameSpan,
        ZType? type
    )
    {
        // Synthesize a Name node for the function/value name itself so the cursor on
        // the name resolves precisely — the outer Define span is single-line and
        // doesn't reach the name on multi-line forms.
        if (nameSpan.Length == 0)
            return [];
        return [new AstNode.Name(name, nameSpan) { ResolvedType = type }];
    }

    private static IEnumerable<AstNode> MethodChildren(ObjectMethod method)
    {
        return ParamNames(method.Params).Append(method.Body);
    }

    private static IEnumerable<AstNode> ConstructorChildren(ConstructorDecl ctor)
    {
        var children = ParamNames(ctor.Params).ToList();
        if (ctor.SuperArgs is not null)
            children.AddRange(ctor.SuperArgs);
        children.AddRange(ctor.FieldSets.Select(f => f.Value));
        children.AddRange(ctor.BodyExprs);
        return children;
    }

    private static IEnumerable<AstNode> ObjectExprChildren(AstNode.ObjectExpr oe)
    {
        var children = oe.Methods.SelectMany(MethodChildren).ToList();
        if (oe.Constructor is not null)
            children.AddRange(ConstructorChildren(oe.Constructor));
        return children;
    }

    private static IEnumerable<AstNode> ClassDeclChildren(AstNode.ClassDecl cd)
    {
        var children = cd.Methods.SelectMany(MethodChildren).ToList();
        if (cd.Constructor is not null)
            children.AddRange(ConstructorChildren(cd.Constructor));
        return children;
    }
}
