using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using static ZScheme.Compiler.Ast.AstScopes;

namespace ZScheme.Compiler.Types;

/// <summary>
///     Flags self-recursive functions that <see cref="Ir.TailCallLowering" /> will not turn
///     into a loop, as <see cref="DiagnosticCodes.NonLoopedSelfRecursion" /> warnings. The
///     message names the reason, because that is what makes it actionable: the recursive call
///     is not in tail position, it is behind a <c>with-handlers</c>/<c>use</c> frame, the
///     function is not a top-level <c>define</c>, or it is <c>async</c> (which the IL backend
///     does not loop).
///
///     This mirrors <see cref="Ir.TailCallLowering" />'s rules on the AST, one stage earlier,
///     so the warning reaches the language server. The AST tail spine — <c>if</c> branches,
///     a <c>let</c> body, <c>match</c> arm bodies — maps 1:1 onto the IR spine the pass walks,
///     and <c>cond</c>/<c>when</c>/<c>begin</c>/multi-body have all desugared into that spine by
///     the time this runs. <c>tests/Pipeline/TailRecursionDriftTests.cs</c> pins the two
///     together: silence here must mean <c>IsTcoLoop</c> there.
///
///     Deliberately quiet where the AST cannot decide:
///     <list type="bullet">
///         <item>Class/object methods and constructors are skipped — a bare <c>(foo x)</c> in a
///             method body does not resolve to the method, so treating it as a self-call would
///             be a false positive.</item>
///         <item>A body containing an immediately-invoked lambda this pass cannot prove reducible
///             is left alone, since <c>IiffeBetaReducer</c> may yet move the call into tail
///             position.</item>
///         <item>Mutual recursion is never reported — the pass only ever loops self-calls.</item>
///         <item>Value bindings — <c>(define f (lambda …))</c> — are not candidates: the form
///             is non-recursive, so a self-reference in the lambda body fails inference as an
///             undefined variable and never reaches this stage.</item>
///         <item>Silence means the function will be marked <c>IsTcoLoop</c>, not that every
///             recursive path is bounded: one tail arm is enough to make it a loop even when a
///             sibling arm still recurses on the stack.</item>
///     </list>
///     Opted out per definition by <c>#:recursive</c>, and wholesale by
///     <c>CompilerOptions.WarnUnloopedRecursion</c> / <c>--no-warn-unlooped-recursion</c> /
///     the manifest's <c>(warn-unlooped-recursion "false")</c>.
/// </summary>
public sealed class TailRecursionAnalyzer(
    DiagnosticBag diagnostics,
    bool warnUnloopedRecursion = true
)
{
    /// <summary>Why a self-call could not be a loop back-edge, in <c>Data[1]</c>.</summary>
    private const string ReasonNotTail = "not-tail";
    private const string ReasonBarrier = "barrier";
    private const string ReasonNotTopLevel = "not-top-level";
    private const string ReasonAsync = "async";

    public void Analyze(AstNode.Program program)
    {
        if (!warnUnloopedRecursion)
            return;

        var topLevel = TopLevelForms(program);
        foreach (var form in topLevel)
            CheckCandidate(form, isTopLevel: true);

        // Nested definitions: never looped, wherever they appear.
        foreach (var form in topLevel)
        foreach (var nested in Descendants(form))
            CheckCandidate(nested, isTopLevel: false);
    }

    /// <summary>Every strict descendant of <paramref name="node" />.</summary>
    private static IEnumerable<AstNode> Descendants(AstNode node)
    {
        foreach (var child in Children(node))
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private void CheckCandidate(AstNode form, bool isTopLevel)
    {
        // Only forms that bind a name to a body can host a name-based self-call. A bare
        // `lambda` has no name to call, and there is no named `let`/`letrec` in the language.
        // `(define f (lambda ...))` is not a candidate: the value form is non-recursive, so a
        // self-reference in the lambda body fails inference as an undefined variable long
        // before this runs.
        var (name, nameSpan, body, isAsync, isMarked) = form switch
        {
            AstNode.Define d => (d.FnName, d.NameSpan, d.Body, false, d.AllowsUnloopedRecursion),
            AstNode.DefineAsync d => (
                d.FnName,
                d.NameSpan,
                d.Body,
                true,
                d.AllowsUnloopedRecursion
            ),
            _ => (null, default(SourceSpan), null, false, false),
        };

        if (name is null || body is null)
            return;
        if (isMarked)
            return;
        // Desugared / macro-synthesized definitions have no name token to point at.
        if (nameSpan.Length == 0)
            return;

        var shadowedByParams = form switch
        {
            AstNode.Define d => d.Params.Any(p => p.Name == name),
            AstNode.DefineAsync d => d.Params.Any(p => p.Name == name),
            _ => false,
        };

        var sites = new List<Site>();
        Walk(body, name, sites, tail: true, Barrier.None, shadowed: shadowedByParams);
        if (sites.Count == 0)
            return;

        var hasCleanTailCall = sites.Any(s => s is { IsTail: true, Barrier: Barrier.None });

        // Conservatism valve: IiffeBetaReducer.CanBetaReduce also declines async, generic,
        // CLR-delegate-typed, variadic and name-capturing lambdas — conditions we cannot
        // evaluate before type inference has finished shaping the IR. If an IIFE we could not
        // see through is present, beta reduction may still land the call in tail position, so
        // stay silent rather than risk warning about a function that does become a loop.
        if (!hasCleanTailCall && ContainsUnprovenIife(body))
            return;

        if (isTopLevel && hasCleanTailCall && !isAsync)
            return;

        var (reason, explanation) = Diagnose(sites, isTopLevel, isAsync);
        diagnostics.Warning(
            $"Self-recursive function '{name}' is not compiled as a loop: {explanation}",
            nameSpan,
            DiagnosticCodes.NonLoopedSelfRecursion,
            [name, reason]
        );
    }

    /// <summary>Picks the most actionable reason, most-fixable first.</summary>
    private static (string Reason, string Explanation) Diagnose(
        List<Site> sites,
        bool isTopLevel,
        bool isAsync
    )
    {
        if (!isTopLevel)
            return (ReasonNotTopLevel, "only top-level 'define' forms become loops");

        // Currently unreachable from valid source: an async body's type is the unwrapped
        // result, so a bare tail `(f …)` yields Task and fails to unify with the other branch
        // ("Type mismatch: 'Unit' vs 'Task'"). Async self-recursion therefore always goes
        // through `await` and lands on ReasonNotTail below. Kept because the backend asymmetry
        // it names is real in TailCallLowering, and would surface the moment the type rules
        // admit the shape.
        var hasCleanTailCall = sites.Any(s => s is { IsTail: true, Barrier: Barrier.None });
        if (hasCleanTailCall && isAsync)
            return (
                ReasonAsync,
                "the IL backend does not compile async self-recursion as a loop "
                    + "(the C# backend does)"
            );

        var tailBarrier = sites.FirstOrDefault(s => s.IsTail)?.Barrier;
        if (tailBarrier is Barrier.Use or Barrier.WithHandlers)
            return (
                ReasonBarrier,
                $"the tail call is inside a '{(tailBarrier is Barrier.Use ? "use" : "with-handlers")}' "
                    + "body, whose frame must stay on the stack until the body returns"
            );

        return (
            ReasonNotTail,
            "the recursive call is not in tail position, so deep recursion can overflow the stack"
        );
    }

    /// <summary>What stands between a syntactically-tail self-call and a loop back-edge.</summary>
    private enum Barrier
    {
        None,
        Use,
        WithHandlers,
    }

    private sealed record Site(bool IsTail, Barrier Barrier);

    /// <summary>
    ///     Collects every call to <paramref name="name" /> in <paramref name="node" />, tagged
    ///     with whether it sits in tail position and what barrier encloses it. The traversed
    ///     tail spine is exactly <see cref="Ir.TailCallLowering" />'s: <c>if</c> branches, a
    ///     <c>let</c> body, and <c>match</c> arm bodies.
    /// </summary>
    private static void Walk(
        AstNode node,
        string name,
        List<Site> sites,
        bool tail,
        Barrier barrier,
        bool shadowed
    )
    {
        // A rebinding of the function's own name means nothing below refers to the function.
        if (shadowed)
        {
            foreach (var child in Children(node))
                Walk(child, name, sites, false, barrier, true);
            return;
        }

        switch (node)
        {
            case AstNode.Apply { Function: AstNode.Name callee } apply when callee.Value == name:
                sites.Add(new Site(tail, barrier));
                // Arguments are evaluated into the parameter slots — never tail position.
                foreach (var arg in apply.Args)
                    Walk(arg, name, sites, false, barrier, false);
                return;

            // IiffeBetaReducer turns `((lambda (p...) body) a...)` into a `let` spine, so the
            // lambda's body inherits this node's tail-ness.
            case AstNode.Apply { Function: AstNode.Lambda lam } apply
                when lam.Params.Count == apply.Args.Count:
                foreach (var arg in apply.Args)
                    Walk(arg, name, sites, false, barrier, false);
                Walk(lam.Body, name, sites, tail, barrier, lam.Params.Any(p => p.Name == name));
                return;

            case AstNode.If ifNode:
                Walk(ifNode.Condition, name, sites, false, barrier, false);
                Walk(ifNode.Then, name, sites, tail, barrier, false);
                Walk(ifNode.Else, name, sites, tail, barrier, false);
                return;

            // `let` is non-recursive, so the value is outside the new scope. This case also
            // covers `begin` and multi-body forms, which desugar to `let` spines.
            case AstNode.Let let:
                Walk(let.Value, name, sites, false, barrier, false);
                Walk(let.Body, name, sites, tail, barrier, let.VarName == name);
                return;

            case AstNode.Match match:
                Walk(match.Scrutinee, name, sites, false, barrier, false);
                foreach (var arm in match.Arms)
                    Walk(arm.Body, name, sites, tail, barrier, PatternBinds(arm.Pattern, name));
                return;

            // Barriers: the resource is disposed / the handler frame unwinds after the body
            // returns, so nothing inside them is a loop back-edge however it is written. Tail
            // position is carried through anyway — a call that is syntactically tail but
            // barred is exactly the case worth naming, so the reason can say which frame.
            case AstNode.Use use:
                Walk(use.Value, name, sites, false, barrier, false);
                Walk(use.Body, name, sites, tail, Barrier.Use, use.VarName == name);
                return;

            case AstNode.WithHandlers withHandlers:
                Walk(withHandlers.Body, name, sites, tail, Barrier.WithHandlers, false);
                foreach (var handler in withHandlers.Handlers)
                    Walk(
                        handler.HandlerBody,
                        name,
                        sites,
                        tail,
                        Barrier.WithHandlers,
                        handler.BindingVarName == name
                    );
                return;

            // Separate function bodies: a call to `name` in here is not a back-edge for the
            // enclosing definition. Its own recursion is checked when it comes up as a
            // candidate in its own right.
            case AstNode.Lambda lambda:
                Walk(
                    lambda.Body,
                    name,
                    sites,
                    false,
                    barrier,
                    lambda.Params.Any(p => p.Name == name)
                );
                return;

            case AstNode.Define define:
                Walk(
                    define.Body,
                    name,
                    sites,
                    false,
                    barrier,
                    define.FnName == name || define.Params.Any(p => p.Name == name)
                );
                return;

            case AstNode.DefineAsync defineAsync:
                Walk(
                    defineAsync.Body,
                    name,
                    sites,
                    false,
                    barrier,
                    defineAsync.FnName == name || defineAsync.Params.Any(p => p.Name == name)
                );
                return;

            case AstNode.DefineValue defineValue:
                Walk(defineValue.Value, name, sites, false, barrier, false);
                return;

            // Await, BinOp-shaped applies, tuples, `with`, `raise`, object/class bodies, ...
            default:
                foreach (var child in Children(node))
                    Walk(child, name, sites, false, barrier, false);
                return;
        }
    }

    /// <summary>
    ///     Whether the body applies a lambda directly in a shape this analyzer did not see
    ///     through — the arity mismatch case, or one nested under a shadowing rebind. Used only
    ///     as a "stay silent" valve, so a conservative <c>true</c> costs nothing but a missed
    ///     warning.
    /// </summary>
    private static bool ContainsUnprovenIife(AstNode node)
    {
        if (
            node is AstNode.Apply { Function: AstNode.Lambda lam } apply
            && lam.Params.Count != apply.Args.Count
        )
            return true;

        return Children(node).Any(ContainsUnprovenIife);
    }
}
