using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Types;

/// <summary>
///     Flags bindings whose name is never referenced, as
///     <see cref="DiagnosticCodes.UnusedBinding" /> warnings:
///     <list type="bullet">
///         <item><c>let</c>/<c>use</c> locals never referenced in their body,</item>
///         <item>parameters (of <c>define</c>/<c>define-async</c>/<c>lambda</c>, class and
///             object methods, and constructors) never referenced in their scope —
///             disabled via <c>CompilerOptions.WarnUnusedParameters</c> /
///             <c>--no-warn-unused-params</c> / the manifest's
///             <c>(warn-unused-params "false")</c>,</item>
///         <item>top-level private definitions: only in programs with at least one
///             <c>(export …)</c> form (scripts and mains stay silent), a non-exported,
///             attribute-free define (other than <c>main</c>) that no <em>other</em>
///             top-level form references — self-recursion doesn't count as use.</item>
///     </list>
///     Scope-aware: an occurrence under a shadowing rebind (inner <c>let</c>/<c>use</c>,
///     a parameter, a match-arm pattern variable, or a handler binding) does not count.
///     Bindings named <c>_</c> or prefixed with <c>_</c> opt out, and desugared
///     bindings (multi-body wrappers, macro-synthesized forms with no
///     <c>NameSpan</c>) are skipped.
/// </summary>
public sealed class UnusedBindingAnalyzer(
    DiagnosticBag diagnostics,
    bool warnUnusedParameters = true
)
{
    public void Analyze(AstNode.Program program)
    {
        Walk(program);
        CheckTopLevelDefines(program);
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
            case AstNode.Lambda lambda:
                CheckParams(lambda.Params, [lambda.Body]);
                break;
            case AstNode.Define define:
                CheckParams(define.Params, [define.Body]);
                break;
            case AstNode.DefineAsync defineAsync:
                CheckParams(defineAsync.Params, [defineAsync.Body]);
                break;
            case AstNode.ObjectExpr objectExpr:
                CheckMethodParams(objectExpr.Methods, objectExpr.Constructor);
                break;
            case AstNode.ClassDecl classDecl:
                CheckMethodParams(classDecl.Methods, classDecl.Constructor);
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

    private void CheckParams(IReadOnlyList<Param> params_, IReadOnlyList<AstNode> scope)
    {
        if (!warnUnusedParameters)
            return;

        foreach (var param in params_)
        {
            var nameSpan = param.NameSpan.Length > 0 ? param.NameSpan : param.Span;
            if (nameSpan.Length == 0 || param.Name.StartsWith('_'))
                continue;
            if (scope.Any(node => IsUsed(node, param.Name)))
                continue;

            diagnostics.Warning(
                $"Unused parameter '{param.Name}'",
                nameSpan,
                DiagnosticCodes.UnusedBinding,
                [param.Name]
            );
        }
    }

    private void CheckMethodParams(
        IReadOnlyList<ObjectMethod> methods,
        ConstructorDecl? constructor
    )
    {
        foreach (var method in methods)
            CheckParams(method.Params, [method.Body]);
        if (constructor is not null)
            CheckParams(constructor.Params, [.. ConstructorScope(constructor)]);
    }

    /// <summary>Flags non-exported top-level defines no other top-level form uses.
    ///     Gated on the program declaring exports at all — without an
    ///     <c>(export …)</c> form "private" is meaningless (scripts, mains).</summary>
    private void CheckTopLevelDefines(AstNode.Program program)
    {
        var forms = TopLevelForms(program);
        var exported = new HashSet<string>(StringComparer.Ordinal);
        var hasExports = false;
        foreach (var form in forms)
            if (form is AstNode.Export export)
            {
                hasExports = true;
                exported.UnionWith(export.Names);
            }

        if (!hasExports)
            return;

        foreach (var form in forms)
        {
            var (name, nameSpan, attributes) = form switch
            {
                AstNode.Define d => (d.FnName, d.NameSpan, d.Attributes),
                AstNode.DefineAsync d => (d.FnName, d.NameSpan, d.Attributes),
                AstNode.DefineValue d => (d.VarName, d.NameSpan, d.Attributes),
                _ => (null, default(SourceSpan), null),
            };

            if (name is null || nameSpan.Length == 0)
                continue;
            if (name == "main" || name.StartsWith('_') || exported.Contains(name))
                continue;
            // Attributes imply an external consumer (entry points, test frameworks, CLR).
            if (attributes is { Count: > 0 })
                continue;
            // Self-recursion must not count as use: check every *other* top-level form.
            if (forms.Any(other => !ReferenceEquals(other, form) && IsUsed(other, name)))
                continue;

            diagnostics.Warning(
                $"Unused private definition '{name}'",
                nameSpan,
                DiagnosticCodes.UnusedBinding,
                [name]
            );
        }
    }

    private static List<AstNode> TopLevelForms(AstNode.Program program)
    {
        var forms = new List<AstNode>();
        foreach (var form in program.TopLevelForms)
            if (form is AstNode.ModuleDecl mod)
                forms.AddRange(mod.Body);
            else
                forms.Add(form);
        return forms;
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
        return ConstructorScope(constructor).Any(node => IsUsed(node, name));
    }

    private static IEnumerable<AstNode> ConstructorScope(ConstructorDecl constructor)
    {
        foreach (var arg in constructor.SuperArgs ?? [])
            yield return arg;
        foreach (var (_, value) in constructor.FieldSets)
            yield return value;
        foreach (var expr in constructor.BodyExprs)
            yield return expr;
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
    ///     visits every binder in the program.</summary>
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
        foreach (var node in ConstructorScope(constructor))
            yield return node;
    }
}
