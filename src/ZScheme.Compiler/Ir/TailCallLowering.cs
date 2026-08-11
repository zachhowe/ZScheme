using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Ir;

/// <summary>
///     Shared IR rewrite, run just before code generation, that turns tail
///     <em>self</em>-calls into <see cref="IrNode.TcoJump" /> back-edges and marks the
///     enclosing function <see cref="IrNode.FuncDef.IsTcoLoop" />. Both backends then emit
///     the body as a loop (C# <c>while(true)</c> + <c>continue</c>, IL a branch back to a
///     start label), so self-recursion runs in constant stack — this is what makes the two
///     backends agree on deep recursion instead of the IL backend overflowing.
///
///     Rewritten are top-level functions and the methods of sealed classes — exactly the ones
///     each backend emits as a full method body whose self-call binds statically. Capturing
///     recursive lambdas have already been lifted to top level by
///     <see cref="ClosureConverter" />; bare nested lambdas keep their existing (non-looped)
///     emission, and an <c>#:open</c> class's methods are left alone because their self-call
///     dispatches virtually (see <see cref="RewriteClass" />). Only self tail calls become
///     jumps — mutual/other tail calls and non-tail self-calls stay plain
///     <see cref="IrNode.Call" />.
///
///     Self-calls are matched by name, but scope-aware: every binder on the tail spine that
///     can rebind the function's own name — a parameter, a <c>let</c>, a <c>match</c> arm
///     pattern — stops the walk, so a call to a local that shadows the name stays a plain
///     call instead of being rewritten into a back-edge to the wrong function.
///     <see cref="Types.TailRecursionAnalyzer" /> shadows identically one stage earlier, which
///     is what keeps the drift contract (analyzer silence &lt;=&gt; <c>IsTcoLoop</c>) true.
///
///     Because it runs after name resolution and the with-handlers/await hoisters, and nothing
///     reconstructs the tree afterward, the produced <see cref="IrNode.TcoJump" /> /
///     <see cref="IrNode.FuncDef.IsTcoLoop" /> reach the emitters untouched — no other pass
///     needs to know about them.
///
///     <c>async</c> functions are rewritten too, on both backends. An async tail self-call can
///     only ever be spelled <c>(await (self …))</c> — a bare <c>(self …)</c> has type Task and
///     will not unify with its sibling branch — so without the <see cref="IrNode.Await" /> case
///     below no async self-recursion would ever loop, and since ZScheme has no <c>while</c>/
///     <c>do</c>/named-<c>let</c>, self-recursion is the only iteration the language offers.
/// </summary>
public sealed class TailCallLowering
{
    /// <summary>
    ///     Rewrites every definition of every inlined source module, the same way
    ///     <see cref="Rewrite" /> does the main IR. Each emitter calls this on its
    ///     <c>importedModules</c> so a module function loops exactly as it would had it been
    ///     written in the main program — without it, only the main IR is lowered and a package
    ///     library (whose main IR is an empty <c>Seq</c>, with every function arriving as an
    ///     imported module) gets no tail-call lowering at all.
    ///
    ///     Idempotent: re-running over an already-lowered tree finds <see cref="IrNode.TcoJump" />
    ///     where the tail self-call was, rewrites nothing, and leaves
    ///     <see cref="IrNode.FuncDef.IsTcoLoop" /> as it stands.
    /// </summary>
    public IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? RewriteModules(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules
    )
    {
        if (modules is null or { Count: 0 })
            return modules;

        return modules
            .Select(m =>
                (m.ClassName, (IReadOnlyList<IrNode>)m.Definitions.Select(Rewrite).ToList())
            )
            .ToList();
    }

    public IrNode Rewrite(IrNode node)
    {
        return node switch
        {
            IrNode.Seq seq => new IrNode.Seq(seq.Nodes.Select(Rewrite).ToList())
            {
                Type = seq.Type,
                Span = seq.Span,
            },
            IrNode.FuncDef func => RewriteFunc(func),
            IrNode.ClassDecl cls => RewriteClass(cls),
            _ => node,
        };
    }

    /// <summary>
    ///     Rewrites tail self-calls in a class's method bodies, so a loop written as a method
    ///     iterates exactly as the same body written as a top-level <c>define</c> does.
    ///
    ///     A bare <c>(M …)</c> in a method body <em>is</em> a resolved reference to the
    ///     enclosing class's <c>M</c>: <see cref="Types.TypeInferer" /> puts sibling methods
    ///     (self included) in scope by bare name, and both emitters resolve such a name to
    ///     <c>this.M</c>. So the name match <see cref="RewriteTail" /> already performs is a
    ///     genuine self-call marker here, under the same shadowing rules.
    ///
    ///     A method is rewritten when its self-call binds statically. Every method of a sealed
    ///     class does — which includes every class lifted from an <c>(object …)</c> — so the
    ///     jump and the call it replaces are one target. An <c>#:open</c> class emits the
    ///     methods the source wrote as <c>virtual</c>/<c>override</c>, so <c>this.M(…)</c> may
    ///     dispatch to a derived override and a back-edge would silently run the base body
    ///     instead; those are left alone. Its <see cref="IrObjectMethod.IsSynthesizedHelper" />
    ///     methods are not, because they are emitted private and non-virtual: nothing can
    ///     override one and no source name can reach it.
    ///     <see cref="Types.TailRecursionAnalyzer" /> mirrors this, which is what keeps the
    ///     drift contract true for methods as well as functions — a synthesized helper has no
    ///     source form for it to judge, and the group it hosts is judged as a letrec binding.
    /// </summary>
    private IrNode.ClassDecl RewriteClass(IrNode.ClassDecl cls)
    {
        var fieldNames = cls.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        return cls with
        {
            Methods = cls
                .Methods.Select(m =>
                    // `IsOpen` is a proxy for "this method's self-call dispatches virtually",
                    // and it is exact for every method the source wrote. A synthesized helper
                    // is emitted private and non-virtual, so nothing can override it and no
                    // source name can reach it: its self-call binds statically on an open class
                    // exactly as any method's does on a sealed one.
                    cls.IsOpen && !m.IsSynthesizedHelper ? m : RewriteMethod(m, fieldNames)
                )
                .ToList(),
        };
    }

    private IrObjectMethod RewriteMethod(IrObjectMethod method, IReadOnlySet<string> fieldNames)
    {
        // A field of the same name wins over the method in both emitters' reference
        // resolution (`this.<Field>` is checked first), and a parameter rebinds the name over
        // the whole body — mirroring RewriteFunc's `shadowedByParams`. Either way the bare
        // name in the body is not this method, so there is nothing to turn into a back-edge.
        var paramNames = method.Params.Select(p => p.Name).ToList();
        if (fieldNames.Contains(method.Name) || paramNames.Contains(method.Name))
            return method;

        var (body, rewrote) = RewriteTail(method.Body, method.Name, paramNames, method.IsAsync);
        return rewrote ? method with { Body = body, IsTcoLoop = true } : method;
    }

    private IrNode.FuncDef RewriteFunc(IrNode.FuncDef func)
    {
        var paramNames = func.Params.Select(p => p.Name).ToList();

        // A parameter named like the function rebinds the name over the whole body, so no
        // call in it refers to the function. Mirrors TailRecursionAnalyzer's
        // `shadowedByParams`, which keeps the drift contract's two halves in agreement.
        if (paramNames.Contains(func.Name))
            return func;

        var (body, rewrote) = RewriteTail(func.Body, func.Name, paramNames, func.IsAsync);
        return rewrote ? func with { Body = body, IsTcoLoop = true } : func;
    }

    /// <summary>
    ///     Rewrites tail self-calls along the tail spine of <paramref name="node" />. Only
    ///     the spine is reconstructed (and only when something changed); non-tail
    ///     sub-expressions are left untouched by reference.
    ///
    ///     <paramref name="isAsync" /> gates the <see cref="IrNode.Await" /> case, which is the
    ///     only shape an async tail self-call can take.
    /// </summary>
    private (IrNode Node, bool Rewrote) RewriteTail(
        IrNode node,
        string funcName,
        IReadOnlyList<string> paramNames,
        bool isAsync
    )
    {
        switch (node)
        {
            case IrNode.Call { Function: IrNode.Var v } call when v.Name == funcName:
                // Tail self-call -> loop back-edge. The args are NOT rewritten: they are
                // non-tail sub-expressions and are evaluated into the parameter slots.
                //
                // Reaching here means the name is still the function's: every binder on the
                // tail spine that could rebind it (parameters, `let`, `match` arm patterns)
                // has already stopped the walk, so a name match is a genuine self-call.
                return (
                    new IrNode.TcoJump(paramNames, call.Args)
                    {
                        Type = call.Type,
                        Span = call.Span,
                    },
                    true
                );

            // `(await (self …))` in tail position. Awaiting a Task the loop is about to produce
            // itself is pure overhead: dropping the await keeps the state machine, its builder
            // and its Task, and collapses N nested MoveNext frames into one. Every genuine
            // suspension point *inside* the body is untouched, so the set of points at which the
            // function can suspend is unchanged — only the number of machines wrapping them.
            //
            // The `let` peel handles the IL backend's hoisted spelling: AwaitHoister
            // A-normalizes a self-call whose own arguments await into
            // `Await(Let(h0, <await>, Call(self, …)))`. The C# backend does not hoist, so
            // without the peel the same source would loop under C# and not under IL. Peeling is
            // sound because a let value is evaluated before the call either way; the let simply
            // joins the tail spine, which both loop emitters already walk.
            case IrNode.Await await
                when isAsync && SelfCallUnderLets(await.Expr, funcName) is { } selfCall:
                return (
                    RebuildLets(
                        await.Expr,
                        new IrNode.TcoJump(paramNames, selfCall.Args)
                        {
                            // The value this node stands in for is the *awaited* result — the
                            // function's unwrapped return type — not the Task the Call produced.
                            Type = await.Type,
                            Span = await.Span == SourceSpan.None ? selfCall.Span : await.Span,
                        }
                    ),
                    true
                );

            case IrNode.If ifNode:
            {
                var (then, a) = RewriteTail(ifNode.Then, funcName, paramNames, isAsync);
                var (els, b) = RewriteTail(ifNode.Else, funcName, paramNames, isAsync);
                if (!a && !b)
                    return (node, false);
                return (
                    new IrNode.If(ifNode.Condition, then, els)
                    {
                        Type = ifNode.Type,
                        Span = ifNode.Span,
                    },
                    true
                );
            }

            case IrNode.Let let:
            {
                // A `let` rebinding the function's own name shadows it for the whole body,
                // so nothing below is a self-call. The bound value is not tail position, so
                // there is nothing left to rewrite here.
                if (let.VarName == funcName)
                    return (node, false);

                var (body, changed) = RewriteTail(let.Body, funcName, paramNames, isAsync);
                if (!changed)
                    return (node, false);
                return (
                    new IrNode.Let(let.VarName, let.Value, body, let.VarType, let.EmitName)
                    {
                        Type = let.Type,
                        Span = let.Span,
                    },
                    true
                );
            }

            case IrNode.Match match:
            {
                var rewrote = false;
                var arms = new List<IrMatchArm>(match.Arms.Count);
                foreach (var arm in match.Arms)
                {
                    // An arm whose pattern binds the function's own name shadows it for that
                    // arm's body only, so the other arms are still walked.
                    if (PatternBinds(arm.Pattern, funcName))
                    {
                        arms.Add(arm);
                        continue;
                    }

                    var (body, changed) = RewriteTail(arm.Body, funcName, paramNames, isAsync);
                    rewrote |= changed;
                    arms.Add(changed ? new IrMatchArm(arm.Pattern, body) : arm);
                }

                if (!rewrote)
                    return (node, false);
                return (
                    new IrNode.Match(match.Scrutinee, arms)
                    {
                        Type = match.Type,
                        Span = match.Span,
                    },
                    true
                );
            }

            case IrNode.Seq seq:
            {
                if (seq.Nodes.Count == 0)
                    return (node, false);
                var (last, changed) = RewriteTail(seq.Nodes[^1], funcName, paramNames, isAsync);
                if (!changed)
                    return (node, false);
                var nodes = seq.Nodes.ToList();
                nodes[^1] = last;
                return (new IrNode.Seq(nodes) { Type = seq.Type, Span = seq.Span }, true);
            }

            // IrNode.Use and IrNode.WithHandlers are tail barriers (disposal / the handler
            // frame runs after the body returns), so no call inside them is in tail position.
            // Everything else is not a tail self-call. In all cases: leave untouched.
            default:
                return (node, false);
        }
    }

    /// <summary>
    ///     The self-call at the bottom of a (possibly empty) <c>let</c> spine, or null when the
    ///     spine bottoms out in anything else. Only a <em>direct</em> self-call qualifies: an
    ///     <c>(await (if … (f …) …))</c> is deliberately not a back-edge, and
    ///     <see cref="Types.TailRecursionAnalyzer" /> mirrors that exclusion.
    /// </summary>
    private static IrNode.Call? SelfCallUnderLets(IrNode node, string funcName)
    {
        while (node is IrNode.Let let)
        {
            // A hoisted binding that rebinds the function's own name shadows it for the rest
            // of the spine, so the call at the bottom is not a self-call.
            if (let.VarName == funcName)
                return null;
            node = let.Body;
        }

        return node is IrNode.Call { Function: IrNode.Var v } call && v.Name == funcName
            ? call
            : null;
    }

    /// <summary>
    ///     Whether <paramref name="pattern" /> binds <paramref name="name" />, which makes the
    ///     arm's body shadow it. The IR mirror of <see cref="Ast.AstScopes.PatternBinds" />.
    /// </summary>
    private static bool PatternBinds(IrPattern pattern, string name) =>
        pattern switch
        {
            IrPattern.Variable v => v.Name == name,
            IrPattern.Constructor c => c.Fields.Any(f => PatternBinds(f, name)),
            IrPattern.Tuple t => t.Elements.Any(e => PatternBinds(e, name)),
            _ => false,
        };

    /// <summary>
    ///     Rebuilds <paramref name="spine" />'s <c>let</c> bindings around
    ///     <paramref name="leaf" />, replacing the self-call at the bottom. Only the spine is
    ///     reconstructed; each bound value is shared by reference.
    /// </summary>
    private static IrNode RebuildLets(IrNode spine, IrNode leaf) =>
        spine is IrNode.Let let
            ? new IrNode.Let(
                let.VarName,
                let.Value,
                RebuildLets(let.Body, leaf),
                let.VarType,
                let.EmitName
            )
            {
                Type = leaf.Type,
                Span = let.Span,
            }
            : leaf;
}
