using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Types;

/// <summary>
///     Flags <c>let</c>/<c>use</c> bindings whose name is never referenced in their
///     body, as <see cref="DiagnosticCodes.UnusedBinding" /> warnings. Scope-aware:
///     an occurrence under a shadowing rebind (inner <c>let</c>/<c>use</c>, a
///     parameter, a match-arm pattern variable, or a handler binding) does not count.
///     Bindings named <c>_</c> or prefixed with <c>_</c> opt out, and desugared
///     bindings (multi-body wrappers, macro-synthesized forms with no
///     <c>NameSpan</c>) are skipped. Parameters and top-level defines are out of
///     scope for now — exports make "unused" ambiguous there.
/// </summary>
public sealed class UnusedBindingAnalyzer(DiagnosticBag diagnostics)
{
    public void Analyze(AstNode.Program program)
    {
        Walk(program);
    }

    private void Walk(AstNode node)
    {
        switch (node)
        {
            case AstNode.Let let:
                CheckBinding(let.VarName, let.NameSpan, let.Body, isUse: false);
                break;
            case AstNode.Use use:
                CheckBinding(use.VarName, use.NameSpan, use.Body, isUse: true);
                break;
        }

        foreach (var child in Children(node))
            Walk(child);
    }

    private void CheckBinding(string name, SourceSpan nameSpan, AstNode body, bool isUse)
    {
        if (nameSpan.Length == 0 || name.StartsWith('_'))
            return;
        if (IsUsed(body, name))
            return;

        diagnostics.Warning(
            isUse
                ? $"Unused binding '{name}' (the resource is still disposed)"
                : $"Unused binding '{name}'",
            nameSpan,
            DiagnosticCodes.UnusedBinding,
            [name]
        );
    }

    /// <summary>Whether <paramref name="name" /> occurs free in <paramref name="node" />
    ///     — occurrences under a rebinding of the same name don't count.</summary>
    private static bool IsUsed(AstNode node, string name)
    {
        switch (node)
        {
            case AstNode.Name n:
                return n.Value == name;

            // let/use are non-recursive: the value is outside the new scope.
            case AstNode.Let let:
                return IsUsed(let.Value, name)
                    || (let.VarName != name && IsUsed(let.Body, name));
            case AstNode.Use use:
                return IsUsed(use.Value, name)
                    || (use.VarName != name && IsUsed(use.Body, name));

            case AstNode.Lambda lambda:
                return lambda.Params.All(p => p.Name != name) && IsUsed(lambda.Body, name);
            case AstNode.Define define:
                return define.Params.All(p => p.Name != name) && IsUsed(define.Body, name);
            case AstNode.DefineAsync defineAsync:
                return defineAsync.Params.All(p => p.Name != name)
                    && IsUsed(defineAsync.Body, name);

            case AstNode.Match match:
                return IsUsed(match.Scrutinee, name)
                    || match.Arms.Any(a =>
                        !PatternBinds(a.Pattern, name) && IsUsed(a.Body, name)
                    );

            case AstNode.WithHandlers withHandlers:
                return IsUsed(withHandlers.Body, name)
                    || withHandlers.Handlers.Any(h =>
                        h.BindingVarName != name && IsUsed(h.HandlerBody, name)
                    );

            case AstNode.ObjectExpr objectExpr:
                return MethodsUse(objectExpr.Methods, objectExpr.Constructor, name);
            case AstNode.ClassDecl classDecl:
                return MethodsUse(classDecl.Methods, classDecl.Constructor, name);

            default:
                return Children(node).Any(child => IsUsed(child, name));
        }
    }

    private static bool MethodsUse(
        IReadOnlyList<ObjectMethod> methods,
        ConstructorDecl? constructor,
        string name
    )
    {
        if (methods.Any(m => m.Params.All(p => p.Name != name) && IsUsed(m.Body, name)))
            return true;
        if (constructor is null || constructor.Params.Any(p => p.Name == name))
            return false;
        return (constructor.SuperArgs ?? []).Any(a => IsUsed(a, name))
            || constructor.FieldSets.Any(f => IsUsed(f.Value, name))
            || constructor.BodyExprs.Any(e => IsUsed(e, name));
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

    /// <summary>Plain child enumeration (no scope logic) driving the outer walk that
    ///     visits every <c>let</c>/<c>use</c> in the program.</summary>
    private static IEnumerable<AstNode> Children(AstNode node)
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
