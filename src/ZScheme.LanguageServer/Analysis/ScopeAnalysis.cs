using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>A local binding visible at a cursor position, for completion.</summary>
internal sealed record LocalBinding(string Name, ZType? Type, SymbolKind Kind);

/// <summary>
///     Scope-aware resolution of local bindings (<c>let</c>/<c>use</c> variables,
///     parameters, match-pattern variables). Unlike the workspace index — which matches
///     same-file references by bare name and therefore conflates shadowed locals — this
///     walks the binding structure of the AST, so rename and document-highlight touch
///     exactly the occurrences bound by one binder. The shadowing rules mirror the
///     compiler's <c>UnusedBindingAnalyzer.IsUsed</c> (Types/UnusedBindingAnalyzer.cs);
///     keep the two in sync when new binder kinds are added.
/// </summary>
internal static class ScopeAnalysis
{
    /// <summary>
    ///     The occurrence spans (binding site plus shadow-respecting uses) of the local
    ///     binding at the 1-based (line, col) cursor, or null when the cursor is not on
    ///     a local — see <see cref="FindBinder" /> for what counts. A rename that cannot
    ///     reach its own binder would produce a broken edit, so the null cases matter
    ///     here too.
    /// </summary>
    public static IReadOnlyList<SourceSpan>? LocalOccurrences(
        AstNode.Program ast,
        int line,
        int col
    )
    {
        var target = FindBinder(ast, line, col);
        return target is null ? null : Occurrences(target);
    }

    /// <summary>
    ///     The declaration span of the local binding at the 1-based (line, col) cursor —
    ///     the parameter, <c>let</c>/<c>use</c> name, or match-pattern variable it is
    ///     bound by — or null when the cursor is not on a local. Drives go-to-definition
    ///     and go-to-declaration for locals, which the file-wide symbol table cannot
    ///     serve: it excludes parameters and conflates same-named locals across
    ///     functions.
    /// </summary>
    public static SourceSpan? BindingSiteAt(AstNode.Program ast, int line, int col)
    {
        return FindBinder(ast, line, col)?.NameSpan;
    }

    /// <summary>
    ///     The binder owning the cursor position, whether it sits on the binding site
    ///     itself or on a use. Returns null when the cursor is not on a local (top-level
    ///     symbols fall through to <see cref="SymbolResolver" />) or when the binder has
    ///     no source span (handler-clause variables, desugared forms) — a caller that
    ///     cannot reach the binder has nothing to navigate to or rewrite.
    /// </summary>
    private static Binder? FindBinder(AstNode.Program ast, int line, int col)
    {
        var binders = CollectBinders(ast);

        // Cursor on a binding site: the name spans are disjoint, so first match wins.
        var target = binders.FirstOrDefault(b => SpanContains(b.NameSpan, line, col));

        if (target is null)
        {
            // Cursor on a use site: find the innermost enclosing binder of that name.
            var path = AstNavigation.PathTo(ast, line, col);
            if (path is null || path.Count == 0 || path[^1] is not AstNode.Name name)
                return null;
            if (name.Span.Length == 0)
                return null;

            for (var i = path.Count - 2; i >= 0 && target is null; i--)
            {
                var (binder, unbindable) = BinderFor(path[i], path[i + 1], name.Value);
                if (unbindable)
                    return null;
                target = binder;
            }

            if (target is null)
                return null;
        }

        return target.NameSpan.Length == 0 ? null : target;
    }

    /// <summary>
    ///     Every occurrence span bound by <em>any</em> local binder of
    ///     <paramref name="name" /> in the file. Rename/highlight of a top-level symbol
    ///     subtract these so shadowing locals of the same name are left untouched.
    /// </summary>
    public static IReadOnlySet<SourceSpan> OccurrencesBoundLocally(
        AstNode.Program ast,
        string name
    )
    {
        var spans = new HashSet<SourceSpan>();
        foreach (var binder in CollectBinders(ast))
            if (binder.Name == name && binder.NameSpan.Length > 0)
                spans.UnionWith(Occurrences(binder));
        return spans;
    }

    /// <summary>
    ///     The local bindings visible at the 1-based (line, col) cursor, innermost
    ///     shadow winning per name. Scope extents come from the lexical bracket tree
    ///     (AST spans are single-line): a binding is in scope when the cursor lies
    ///     inside its owning form's bracket extent, at or after the end of the binding
    ///     name. For <c>let</c>/<c>use</c> this over-approximates into the bound value
    ///     (the binding is not in scope there), an accepted simplification.
    /// </summary>
    public static IReadOnlyList<LocalBinding> BindingsInScopeAt(
        AstNode.Program ast,
        string source,
        int line,
        int col
    )
    {
        var extents = BracketExtents(source);
        // Innermost wins per name; forms that start later are nested deeper (or at
        // least closer), so the greatest form-start position is the innermost binder.
        var best = new Dictionary<string, (Binder Binder, int Line, int Col)>(
            StringComparer.Ordinal
        );

        foreach (var binder in CollectBinders(ast))
        {
            if (binder.NameSpan.Length == 0)
                continue;
            if (!extents.TryGetValue((binder.FormSpan.Line, binder.FormSpan.Column), out var end))
                continue;

            var afterName =
                line > binder.NameSpan.Line
                || (
                    line == binder.NameSpan.Line
                    && col >= binder.NameSpan.Column + binder.NameSpan.Length
                );
            var beforeEnd = line < end.Line || (line == end.Line && col <= end.Col);
            if (!afterName || !beforeEnd)
                continue;

            if (
                !best.TryGetValue(binder.Name, out var current)
                || binder.FormSpan.Line > current.Line
                || (binder.FormSpan.Line == current.Line && binder.FormSpan.Column > current.Col)
            )
                best[binder.Name] = (binder, binder.FormSpan.Line, binder.FormSpan.Column);
        }

        return [.. best.Values.Select(v => new LocalBinding(v.Binder.Name, v.Binder.Type, v.Binder.Kind))];
    }

    /// <summary>A local binder: the bound name, where it is declared, and the nodes
    ///     that make up its scope. <c>FormSpan</c> is the owning form's span, used to
    ///     recover a multi-line scope extent from the lexical bracket tree.</summary>
    private sealed record Binder(
        string Name,
        SourceSpan NameSpan,
        ZType? Type,
        SymbolKind Kind,
        IReadOnlyList<AstNode> Scope,
        SourceSpan FormSpan
    );

    private static List<Binder> CollectBinders(AstNode.Program ast)
    {
        var binders = new List<Binder>();
        Collect(ast, binders);
        return binders;
    }

    private static void Collect(AstNode node, List<Binder> binders)
    {
        switch (node)
        {
            case AstNode.Let let:
                binders.Add(
                    new Binder(
                        let.VarName,
                        let.NameSpan,
                        let.Value.ResolvedType ?? let.TypeAnnotation,
                        SymbolKind.Variable,
                        [let.Body],
                        let.Span
                    )
                );
                break;
            case AstNode.Use use:
                binders.Add(
                    new Binder(
                        use.VarName,
                        use.NameSpan,
                        use.Value.ResolvedType ?? use.TypeAnnotation,
                        SymbolKind.Variable,
                        [use.Body],
                        use.Span
                    )
                );
                break;
            case AstNode.Lambda lambda:
                AddParamBinders(binders, lambda.Params, [lambda.Body], lambda.Span);
                break;
            case AstNode.Define define:
                AddParamBinders(binders, define.Params, [define.Body], define.Span);
                break;
            case AstNode.DefineAsync defineAsync:
                AddParamBinders(binders, defineAsync.Params, [defineAsync.Body], defineAsync.Span);
                break;
            case AstNode.Match match:
                foreach (var arm in match.Arms)
                    AddPatternBinders(binders, arm.Pattern, arm);
                break;
            case AstNode.ObjectExpr objectExpr:
                AddMethodBinders(binders, objectExpr.Methods, objectExpr.Constructor);
                break;
            case AstNode.ClassDecl classDecl:
                AddMethodBinders(binders, classDecl.Methods, classDecl.Constructor);
                break;
            // HandlerClause binding variables have no source span on the AST, so they
            // are never binder targets — but they still shadow during occurrence
            // collection (see CollectUses).
        }

        foreach (var child in PlainChildren(node))
            Collect(child, binders);
    }

    private static void AddParamBinders(
        List<Binder> binders,
        IReadOnlyList<Param> params_,
        IReadOnlyList<AstNode> scope,
        SourceSpan formSpan
    )
    {
        foreach (var p in params_)
            binders.Add(
                new Binder(
                    p.Name,
                    ParamNameSpan(p),
                    p.ResolvedType ?? p.TypeAnnotation,
                    SymbolKind.Parameter,
                    scope,
                    formSpan
                )
            );
    }

    private static void AddPatternBinders(List<Binder> binders, Pattern pattern, MatchArm arm)
    {
        switch (pattern)
        {
            case Pattern.Variable v:
                binders.Add(
                    new Binder(
                        v.Name,
                        v.Span,
                        v.ResolvedType,
                        SymbolKind.Variable,
                        [arm.Body],
                        arm.Span
                    )
                );
                break;
            case Pattern.Constructor c:
                foreach (var field in c.Fields)
                    AddPatternBinders(binders, field, arm);
                break;
            case Pattern.Tuple t:
                foreach (var element in t.Elements)
                    AddPatternBinders(binders, element, arm);
                break;
        }
    }

    private static void AddMethodBinders(
        List<Binder> binders,
        IReadOnlyList<ObjectMethod> methods,
        ConstructorDecl? constructor
    )
    {
        foreach (var method in methods)
            AddParamBinders(binders, method.Params, [method.Body], method.Span);
        if (constructor is not null)
            AddParamBinders(binders, constructor.Params, ConstructorScope(constructor), constructor.Span);
    }

    private static IReadOnlyList<AstNode> ConstructorScope(ConstructorDecl constructor)
    {
        var scope = new List<AstNode>();
        scope.AddRange(constructor.SuperArgs ?? []);
        scope.AddRange(constructor.FieldSets.Select(f => f.Value));
        scope.AddRange(constructor.BodyExprs);
        return scope;
    }

    /// <summary>
    ///     The binder that <paramref name="parent" /> establishes for
    ///     <paramref name="name" /> over the subtree entered via <paramref name="child" />
    ///     (a node on the cursor path). <c>Unbindable</c> is set when the name is bound
    ///     here but the binder has no source span to navigate to or rewrite (handler
    ///     clauses) — the search must stop rather than fall through to an outer binder.
    /// </summary>
    private static (Binder? Binder, bool Unbindable) BinderFor(
        AstNode parent,
        AstNode child,
        string name
    )
    {
        switch (parent)
        {
            case AstNode.Let let when ReferenceEquals(child, let.Body) && let.VarName == name:
                return (
                    new Binder(
                        let.VarName,
                        let.NameSpan,
                        let.Value.ResolvedType ?? let.TypeAnnotation,
                        SymbolKind.Variable,
                        [let.Body],
                        let.Span
                    ),
                    let.NameSpan.Length == 0
                );

            case AstNode.Use use when ReferenceEquals(child, use.Body) && use.VarName == name:
                return (
                    new Binder(
                        use.VarName,
                        use.NameSpan,
                        use.Value.ResolvedType ?? use.TypeAnnotation,
                        SymbolKind.Variable,
                        [use.Body],
                        use.Span
                    ),
                    use.NameSpan.Length == 0
                );

            case AstNode.Lambda lambda when ReferenceEquals(child, lambda.Body):
                return ParamBinder(lambda.Params, name, [lambda.Body], lambda.Span);

            case AstNode.Define define when ReferenceEquals(child, define.Body):
                return ParamBinder(define.Params, name, [define.Body], define.Span);

            case AstNode.DefineAsync defineAsync when ReferenceEquals(child, defineAsync.Body):
                return ParamBinder(
                    defineAsync.Params,
                    name,
                    [defineAsync.Body],
                    defineAsync.Span
                );

            case AstNode.Match match:
            {
                var arm = match.Arms.FirstOrDefault(a => ReferenceEquals(a.Body, child));
                if (arm is null)
                    return (null, false);
                var variable = FindPatternVariable(arm.Pattern, name);
                return variable is null
                    ? (null, false)
                    : (
                        new Binder(
                            variable.Name,
                            variable.Span,
                            variable.ResolvedType,
                            SymbolKind.Variable,
                            [arm.Body],
                            arm.Span
                        ),
                        false
                    );
            }

            case AstNode.WithHandlers withHandlers:
            {
                var handler = withHandlers.Handlers.FirstOrDefault(h =>
                    ReferenceEquals(h.HandlerBody, child)
                );
                // Handler binding variables carry no span — bound here means unrenameable.
                return (null, handler?.BindingVarName == name);
            }

            case AstNode.ObjectExpr objectExpr:
                return MethodBinder(objectExpr.Methods, objectExpr.Constructor, child, name);
            case AstNode.ClassDecl classDecl:
                return MethodBinder(classDecl.Methods, classDecl.Constructor, child, name);

            default:
                return (null, false);
        }
    }

    private static (Binder?, bool) ParamBinder(
        IReadOnlyList<Param> params_,
        string name,
        IReadOnlyList<AstNode> scope,
        SourceSpan formSpan
    )
    {
        var param = params_.FirstOrDefault(p => p.Name == name);
        if (param is null)
            return (null, false);
        var nameSpan = ParamNameSpan(param);
        return (
            new Binder(
                param.Name,
                nameSpan,
                param.ResolvedType ?? param.TypeAnnotation,
                SymbolKind.Parameter,
                scope,
                formSpan
            ),
            nameSpan.Length == 0
        );
    }

    /// <summary>A param's Span covers the whole [name : Type] bracket; the name atom
    ///     span is what rename/highlight must touch.</summary>
    private static SourceSpan ParamNameSpan(Param param)
    {
        return param.NameSpan.Length > 0 ? param.NameSpan : param.Span;
    }

    private static (Binder?, bool) MethodBinder(
        IReadOnlyList<ObjectMethod> methods,
        ConstructorDecl? constructor,
        AstNode child,
        string name
    )
    {
        foreach (var method in methods)
            if (ReferenceEquals(method.Body, child))
                return ParamBinder(method.Params, name, [method.Body], method.Span);

        if (constructor is not null)
        {
            var scope = ConstructorScope(constructor);
            if (scope.Any(n => ReferenceEquals(n, child)))
                return ParamBinder(constructor.Params, name, scope, constructor.Span);
        }

        return (null, false);
    }

    private static Pattern.Variable? FindPatternVariable(Pattern pattern, string name)
    {
        return pattern switch
        {
            Pattern.Variable v when v.Name == name => v,
            Pattern.Constructor c => c
                .Fields.Select(f => FindPatternVariable(f, name))
                .FirstOrDefault(v => v is not null),
            Pattern.Tuple t => t
                .Elements.Select(e => FindPatternVariable(e, name))
                .FirstOrDefault(v => v is not null),
            _ => null,
        };
    }

    private static IReadOnlyList<SourceSpan> Occurrences(Binder binder)
    {
        var spans = new List<SourceSpan> { binder.NameSpan };
        foreach (var node in binder.Scope)
            CollectUses(node, binder.Name, spans);
        return spans.Distinct().ToList();
    }

    /// <summary>Adds the spans of free occurrences of <paramref name="name" /> in
    ///     <paramref name="node" /> — occurrences under a rebinding of the same name
    ///     don't count. Mirrors <c>UnusedBindingAnalyzer.IsUsed</c>.</summary>
    private static void CollectUses(AstNode node, string name, List<SourceSpan> spans)
    {
        switch (node)
        {
            case AstNode.Name n:
                if (n.Value == name && n.Span.Length > 0)
                    spans.Add(n.Span);
                return;

            // let/use are non-recursive: the value is outside the new scope.
            case AstNode.Let let:
                CollectUses(let.Value, name, spans);
                if (let.VarName != name)
                    CollectUses(let.Body, name, spans);
                return;
            case AstNode.Use use:
                CollectUses(use.Value, name, spans);
                if (use.VarName != name)
                    CollectUses(use.Body, name, spans);
                return;

            case AstNode.Lambda lambda:
                if (lambda.Params.All(p => p.Name != name))
                    CollectUses(lambda.Body, name, spans);
                return;
            case AstNode.Define define:
                if (define.Params.All(p => p.Name != name))
                    CollectUses(define.Body, name, spans);
                return;
            case AstNode.DefineAsync defineAsync:
                if (defineAsync.Params.All(p => p.Name != name))
                    CollectUses(defineAsync.Body, name, spans);
                return;

            case AstNode.Match match:
                CollectUses(match.Scrutinee, name, spans);
                foreach (var arm in match.Arms)
                    if (!PatternBinds(arm.Pattern, name))
                        CollectUses(arm.Body, name, spans);
                return;

            case AstNode.WithHandlers withHandlers:
                CollectUses(withHandlers.Body, name, spans);
                foreach (var handler in withHandlers.Handlers)
                    if (handler.BindingVarName != name)
                        CollectUses(handler.HandlerBody, name, spans);
                return;

            case AstNode.ObjectExpr objectExpr:
                CollectMethodUses(objectExpr.Methods, objectExpr.Constructor, name, spans);
                return;
            case AstNode.ClassDecl classDecl:
                CollectMethodUses(classDecl.Methods, classDecl.Constructor, name, spans);
                return;

            default:
                foreach (var child in PlainChildren(node))
                    CollectUses(child, name, spans);
                return;
        }
    }

    private static void CollectMethodUses(
        IReadOnlyList<ObjectMethod> methods,
        ConstructorDecl? constructor,
        string name,
        List<SourceSpan> spans
    )
    {
        foreach (var method in methods)
            if (method.Params.All(p => p.Name != name))
                CollectUses(method.Body, name, spans);

        if (constructor is null || constructor.Params.Any(p => p.Name == name))
            return;
        foreach (var node in ConstructorScope(constructor))
            CollectUses(node, name, spans);
    }

    private static bool PatternBinds(Pattern pattern, string name)
    {
        return pattern switch
        {
            Pattern.Variable v => v.Name == name,
            Pattern.Constructor c => c.Fields.Any(f => PatternBinds(f, name)),
            Pattern.Tuple t => t.Elements.Any(e => PatternBinds(e, name)),
            _ => false,
        };
    }

    /// <summary>Multi-line extents of every bracketed form, keyed by the 1-based
    ///     (line, column) of the opening bracket — AST form spans start there, so this
    ///     maps a form span to its true end position.</summary>
    private static Dictionary<(int Line, int Col), (int Line, int Col)> BracketExtents(
        string source
    )
    {
        var extents = new Dictionary<(int, int), (int, int)>();
        var tree = LexicalStructure.BuildTree(LexicalStructure.Tokens(source));

        void Walk(BracketNode bracket)
        {
            extents[(bracket.Open.Span.Line, bracket.Open.Span.Column)] = (
                bracket.Close.Span.Line,
                bracket.Close.Span.Column
            );
            foreach (var child in bracket.Children)
                Walk(child);
        }

        foreach (var top in tree)
            Walk(top);
        return extents;
    }

    private static bool SpanContains(SourceSpan span, int line, int col)
    {
        return span.Length > 0
            && span.Line == line
            && col >= span.Column
            && col < span.Column + span.Length;
    }

    /// <summary>Plain child enumeration (no synthesized name nodes) driving binder
    ///     collection and occurrence pruning. Mirrors the compiler's
    ///     <c>UnusedBindingAnalyzer.Children</c>; keep in sync when node kinds change.</summary>
    private static IEnumerable<AstNode> PlainChildren(AstNode node)
    {
        return node switch
        {
            AstNode.Program p => p.TopLevelForms,
            AstNode.ModuleDecl m => m.Body,
            AstNode.Define d => [d.Body],
            AstNode.DefineAsync d => [d.Body],
            AstNode.DefineValue d => [d.Value],
            AstNode.Let l => [l.Value, l.Body],
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

    private static IEnumerable<AstNode> ObjectChildren(
        IReadOnlyList<ObjectMethod> methods,
        ConstructorDecl? constructor
    )
    {
        foreach (var method in methods)
            yield return method.Body;
        if (constructor is null)
            yield break;
        foreach (var arg in constructor.SuperArgs ?? [])
            yield return arg;
        foreach (var (_, value) in constructor.FieldSets)
            yield return value;
        foreach (var expr in constructor.BodyExprs)
            yield return expr;
    }
}
