namespace ZScheme.Compiler.Ir;

/// <summary>
///     Shared IR rewrite, run just before code generation, that turns tail
///     <em>self</em>-calls into <see cref="IrNode.TcoJump" /> back-edges and marks the
///     enclosing function <see cref="IrNode.FuncDef.IsTcoLoop" />. Both backends then emit
///     the body as a loop (C# <c>while(true)</c> + <c>continue</c>, IL a branch back to a
///     start label), so self-recursion runs in constant stack — this is what makes the two
///     backends agree on deep recursion instead of the IL backend overflowing.
///
///     Only top-level functions are rewritten: those are exactly the ones each backend emits
///     as a full method body. Capturing recursive lambdas have already been lifted to top
///     level by <see cref="ClosureConverter" />; bare nested lambdas keep their existing
///     (non-looped) emission. Only self tail calls become jumps — mutual/other tail calls and
///     non-tail self-calls stay plain <see cref="IrNode.Call" />.
///
///     Because it runs after name resolution and the with-handlers/await hoisters, and nothing
///     reconstructs the tree afterward, the produced <see cref="IrNode.TcoJump" /> /
///     <see cref="IrNode.FuncDef.IsTcoLoop" /> reach the emitters untouched — no other pass
///     needs to know about them.
///
///     <paramref name="includeAsync" /> controls whether self-recursive <c>async</c> functions
///     are rewritten. The C# backend passes <c>true</c> (its loop emitter handles <c>await</c>,
///     preserving the pre-existing async-TCO behavior); the IL backend passes <c>false</c>
///     because its async state-machine emitter cannot consume a <see cref="IrNode.TcoJump" /> —
///     those functions keep their plain recursive emission there.
/// </summary>
public sealed class TailCallLowering(bool includeAsync)
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
            _ => node,
        };
    }

    private IrNode.FuncDef RewriteFunc(IrNode.FuncDef func)
    {
        if (func.IsAsync && !includeAsync)
            return func;

        var paramNames = func.Params.Select(p => p.Name).ToList();
        var (body, rewrote) = RewriteTail(func.Body, func.Name, paramNames);
        return rewrote ? func with { Body = body, IsTcoLoop = true } : func;
    }

    /// <summary>
    ///     Rewrites tail self-calls along the tail spine of <paramref name="node" />. Only
    ///     the spine is reconstructed (and only when something changed); non-tail
    ///     sub-expressions are left untouched by reference.
    /// </summary>
    private (IrNode Node, bool Rewrote) RewriteTail(
        IrNode node,
        string funcName,
        IReadOnlyList<string> paramNames
    )
    {
        switch (node)
        {
            case IrNode.Call { Function: IrNode.Var v } call when v.Name == funcName:
                // Tail self-call -> loop back-edge. The args are NOT rewritten: they are
                // non-tail sub-expressions and are evaluated into the parameter slots.
                //
                // Matches by name only, exactly like the C# backend already did
                // (v.Name == funcName). Polymorphic self-recursion (f<T> calling f<int>)
                // would be miscompiled by a name-based jump; that limitation is shared by
                // both backends and predates this pass.
                return (
                    new IrNode.TcoJump(paramNames, call.Args)
                    {
                        Type = call.Type,
                        Span = call.Span,
                    },
                    true
                );

            case IrNode.If ifNode:
            {
                var (then, a) = RewriteTail(ifNode.Then, funcName, paramNames);
                var (els, b) = RewriteTail(ifNode.Else, funcName, paramNames);
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
                var (body, changed) = RewriteTail(let.Body, funcName, paramNames);
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
                    var (body, changed) = RewriteTail(arm.Body, funcName, paramNames);
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
                var (last, changed) = RewriteTail(seq.Nodes[^1], funcName, paramNames);
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
}
