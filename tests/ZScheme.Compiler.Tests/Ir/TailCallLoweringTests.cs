using Xunit;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class TailCallLoweringTests
{
    private static IrNode.FuncDef Rewrite(IrNode.FuncDef func) =>
        (IrNode.FuncDef)new TailCallLowering().Rewrite(func);

    private static IrNode.Var Var(string name) => new(name) { Type = ZType.Int };

    private static IrNode.Call Call(string name, params IrNode[] args) =>
        new(Var(name), args) { Type = ZType.Int };

    /// <summary>
    ///     `(await (f …))` — the awaited-self-call shape, whose Await carries the *unwrapped*
    ///     result type while the inner Call carries the Task type.
    /// </summary>
    private static IrNode.Await AwaitCall(string name, params IrNode[] args) =>
        new(new IrNode.Call(Var(name), args) { Type = new ZType.ZNamedType("Task", [ZType.Int]) })
        {
            Type = ZType.Int,
        };

    private static IrNode.FuncDef Func(string name, IrNode body, bool isAsync = false) =>
        new(
            name,
            [new IrParam("n", ZType.Int), new IrParam("acc", ZType.Int)],
            ZType.Int,
            body,
            true,
            IsAsync: isAsync
        )
        {
            Type = ZType.Int,
        };

    [Fact]
    public void RewritesTailSelfCall_InIfElse()
    {
        // factorial(n, acc) = if (n == 0) acc else factorial(n-1, n*acc)
        var body = new IrNode.If(
            new IrNode.BinOp("=", Var("n"), new IrNode.IntConst(0) { Type = ZType.Int })
            {
                Type = ZType.Bool,
            },
            Var("acc"),
            Call(
                "factorial",
                new IrNode.BinOp("-", Var("n"), new IrNode.IntConst(1) { Type = ZType.Int })
                {
                    Type = ZType.Int,
                },
                new IrNode.BinOp("*", Var("n"), Var("acc")) { Type = ZType.Int }
            )
        )
        {
            Type = ZType.Int,
        };

        var result = Rewrite(Func("factorial", body));

        Assert.True(result.IsTcoLoop);
        var rewrittenIf = Assert.IsType<IrNode.If>(result.Body);
        var jump = Assert.IsType<IrNode.TcoJump>(rewrittenIf.Else);
        Assert.Equal(["n", "acc"], jump.ParamNames);
        Assert.Equal(2, jump.NewArgs.Count);
    }

    [Fact]
    public void RewritesTailSelfCall_InLetBody()
    {
        var body = new IrNode.Let(
            "y",
            new IrNode.IntConst(5) { Type = ZType.Int },
            Call("factorial", Var("y"), Var("acc"))
        )
        {
            Type = ZType.Int,
        };

        var result = Rewrite(Func("factorial", body));

        Assert.True(result.IsTcoLoop);
        var let = Assert.IsType<IrNode.Let>(result.Body);
        Assert.IsType<IrNode.TcoJump>(let.Body);
    }

    [Fact]
    public void RewritesTailSelfCall_InMatchArm()
    {
        var body = new IrNode.Match(
            Var("n"),
            [new IrMatchArm(new IrPattern.Wildcard(), Call("factorial", Var("n"), Var("acc")))]
        )
        {
            Type = ZType.Int,
        };

        var result = Rewrite(Func("factorial", body));

        Assert.True(result.IsTcoLoop);
        var match = Assert.IsType<IrNode.Match>(result.Body);
        Assert.IsType<IrNode.TcoJump>(match.Arms[0].Body);
    }

    [Fact]
    public void RewritesTailSelfCall_InSeqLastNode()
    {
        // The last node of a `begin` is in tail position; earlier nodes are not. This is the
        // Seq case the old marker was missing entirely.
        var body = new IrNode.Seq([
            new IrNode.IntConst(0) { Type = ZType.Int },
            Call("factorial", Var("n"), Var("acc")),
        ])
        {
            Type = ZType.Int,
        };

        var result = Rewrite(Func("factorial", body));

        Assert.True(result.IsTcoLoop);
        var seq = Assert.IsType<IrNode.Seq>(result.Body);
        Assert.IsType<IrNode.TcoJump>(seq.Nodes[^1]);
    }

    [Fact]
    public void DoesNotRewriteNonTailSelfCall()
    {
        // bad(n, acc) = bad(n-1, acc) + 1 — the call's result is consumed by `+`, so it is not
        // in tail position and stays a plain Call.
        var body = new IrNode.BinOp(
            "+",
            Call(
                "bad",
                new IrNode.BinOp("-", Var("n"), new IrNode.IntConst(1) { Type = ZType.Int })
                {
                    Type = ZType.Int,
                },
                Var("acc")
            ),
            new IrNode.IntConst(1) { Type = ZType.Int }
        )
        {
            Type = ZType.Int,
        };

        var result = Rewrite(Func("bad", body));

        Assert.False(result.IsTcoLoop);
        Assert.IsType<IrNode.BinOp>(result.Body);
    }

    [Fact]
    public void DoesNotRewriteCall_InIfCondition()
    {
        var body = new IrNode.If(
            Call("f", Var("n"), Var("acc")),
            new IrNode.IntConst(1) { Type = ZType.Int },
            new IrNode.IntConst(0) { Type = ZType.Int }
        )
        {
            Type = ZType.Int,
        };

        var result = Rewrite(Func("f", body));

        Assert.False(result.IsTcoLoop);
        var @if = Assert.IsType<IrNode.If>(result.Body);
        Assert.IsType<IrNode.Call>(@if.Condition);
    }

    [Fact]
    public void DoesNotRewriteNonSelfTailCall()
    {
        // Unlike the old marker (which flagged every tail call), only *self* calls become loops.
        // A tail call to another function stays a plain Call.
        var body = Call("other", Var("n"), Var("acc"));

        var result = Rewrite(Func("f", body));

        Assert.False(result.IsTcoLoop);
        Assert.IsType<IrNode.Call>(result.Body);
    }

    #region Shadowed self-names

    [Fact]
    public void DoesNotRewriteCallToLetThatShadowsSelfName()
    {
        // `(let ([f …]) (f …))` inside `f`: the callee is the local, not the function. A
        // back-edge here jumps to the top of `f` and never calls the bound value.
        var body = new IrNode.Let("f", Var("acc"), Call("f", Var("n"), Var("acc")), ZType.Int)
        {
            Type = ZType.Int,
        };

        var result = Rewrite(Func("f", body));

        Assert.False(result.IsTcoLoop);
        Assert.IsType<IrNode.Call>(Assert.IsType<IrNode.Let>(result.Body).Body);
    }

    [Fact]
    public void DoesNotRewriteCallToMatchArmBinderThatShadowsSelfName()
    {
        // The arm's pattern binds `f`, so the arm body's `(f …)` is the binder. Sibling arms
        // are unaffected, which is why the shadow check is per-arm rather than per-match.
        var shadowing = new IrMatchArm(
            new IrPattern.Constructor("B", [new IrPattern.Variable("f")]),
            Call("f", Var("n"), Var("acc"))
        );
        var unshadowed = new IrMatchArm(
            new IrPattern.Constructor("C", [new IrPattern.Variable("m")]),
            Call("f", Var("n"), Var("acc"))
        );
        var body = new IrNode.Match(Var("n"), [shadowing, unshadowed]) { Type = ZType.Int };

        var result = Rewrite(Func("f", body));

        // The unshadowed arm still loops; the shadowed one stays a plain call.
        Assert.True(result.IsTcoLoop);
        var match = Assert.IsType<IrNode.Match>(result.Body);
        Assert.IsType<IrNode.Call>(match.Arms[0].Body);
        Assert.IsType<IrNode.TcoJump>(match.Arms[1].Body);
    }

    [Fact]
    public void DoesNotRewriteCallToParameterThatShadowsSelfName()
    {
        // A parameter named like the function rebinds the name over the entire body.
        var func = new IrNode.FuncDef(
            "f",
            [new IrParam("f", ZType.Int)],
            ZType.Int,
            Call("f", Var("f")),
            true
        )
        {
            Type = ZType.Int,
        };

        var result = Rewrite(func);

        Assert.False(result.IsTcoLoop);
        Assert.IsType<IrNode.Call>(result.Body);
    }

    [Fact]
    public void DoesNotRewriteAwaitedCallUnderALetThatShadowsSelfName()
    {
        // The async spelling of the same bug: AwaitHoister's `let` spine can itself rebind the
        // name, so the call at the bottom of the spine is not a self-call.
        var body = IfBaseCase(
            new IrNode.Await(
                new IrNode.Let(
                    "f",
                    Var("acc"),
                    new IrNode.Call(Var("f"), [Var("n"), Var("acc")])
                    {
                        Type = new ZType.ZNamedType("Task", [ZType.Int]),
                    },
                    ZType.Int
                )
                {
                    Type = new ZType.ZNamedType("Task", [ZType.Int]),
                }
            )
            {
                Type = ZType.Int,
            }
        );

        var result = Rewrite(Func("f", body, isAsync: true));

        Assert.False(result.IsTcoLoop);
        Assert.IsType<IrNode.Await>(Assert.IsType<IrNode.If>(result.Body).Else);
    }

    #endregion

    #region Awaited self-calls (async TCO)

    /// <summary>Wraps <paramref name="tail" /> as the else-branch of an `n = 0` base case.</summary>
    private static IrNode.If IfBaseCase(IrNode tail) =>
        new(
            new IrNode.BinOp("=", Var("n"), new IrNode.IntConst(0) { Type = ZType.Int })
            {
                Type = ZType.Bool,
            },
            Var("acc"),
            tail
        )
        {
            Type = ZType.Int,
        };

    [Fact]
    public void RewritesAwaitedTailSelfCall_InIfElse()
    {
        // The only shape async self-recursion can take: a bare tail `(f …)` has type Task and
        // will not unify with the sibling branch, so it always goes through `await`.
        var result = Rewrite(
            Func("f", IfBaseCase(AwaitCall("f", Var("n"), Var("acc"))), isAsync: true)
        );

        Assert.True(result.IsTcoLoop);
        var jump = Assert.IsType<IrNode.TcoJump>(Assert.IsType<IrNode.If>(result.Body).Else);
        Assert.Equal(["n", "acc"], jump.ParamNames);
        Assert.Equal(2, jump.NewArgs.Count);
    }

    [Fact]
    public void AwaitedTailSelfCall_CarriesTheAwaitsUnwrappedType()
    {
        // The node stands in for the value the *await* produced (the function's unwrapped
        // return type), not the Task the Call produced.
        var result = Rewrite(
            Func("f", IfBaseCase(AwaitCall("f", Var("n"), Var("acc"))), isAsync: true)
        );

        var jump = Assert.IsType<IrNode.TcoJump>(Assert.IsType<IrNode.If>(result.Body).Else);
        Assert.Equal(ZType.Int, jump.Type);
    }

    [Fact]
    public void PeelsHoistedLetSpine_UnderAwait()
    {
        // AwaitHoister A-normalizes a self-call whose own arguments await, so on the IL backend
        // the tail arrives as `Await(Let(h0, <await>, Call(self, [h0])))`. The C# backend does
        // not hoist, so without this peel the same source would loop under C# and not under IL.
        var hoisted = new IrNode.Let(
            "h0",
            new IrNode.Await(
                new IrNode.Call(Var("g"), []) { Type = new ZType.ZNamedType("Task", [ZType.Int]) }
            )
            {
                Type = ZType.Int,
            },
            new IrNode.Call(Var("f"), [Var("h0"), Var("acc")])
            {
                Type = new ZType.ZNamedType("Task", [ZType.Int]),
            },
            ZType.Int
        )
        {
            Type = new ZType.ZNamedType("Task", [ZType.Int]),
        };
        var body = IfBaseCase(new IrNode.Await(hoisted) { Type = ZType.Int });

        var result = Rewrite(Func("f", body, isAsync: true));

        Assert.True(result.IsTcoLoop);
        var let = Assert.IsType<IrNode.Let>(Assert.IsType<IrNode.If>(result.Body).Else);
        Assert.Equal("h0", let.VarName);
        Assert.IsType<IrNode.TcoJump>(let.Body);
    }

    [Fact]
    public void AwaitedNonTailSelfCall_IsNotRewritten()
    {
        // `(+ 1 (await (f …)))` — the awaited result is consumed, so the frame must survive.
        var body = new IrNode.BinOp(
            "+",
            new IrNode.IntConst(1) { Type = ZType.Int },
            AwaitCall("f", Var("n"), Var("acc"))
        )
        {
            Type = ZType.Int,
        };

        Assert.False(Rewrite(Func("f", body, isAsync: true)).IsTcoLoop);
    }

    [Fact]
    public void AwaitOfNonSelfCall_IsNotRewritten()
    {
        var body = IfBaseCase(AwaitCall("other", Var("n"), Var("acc")));

        Assert.False(Rewrite(Func("f", body, isAsync: true)).IsTcoLoop);
    }

    [Fact]
    public void AwaitedSelfCall_InSyncFunction_IsNotRewritten()
    {
        // Unreachable from valid source (`await` outside an async context is a type error), but
        // the isAsync gate documents the intent and protects hand-built IR and the fuzzer.
        var body = IfBaseCase(AwaitCall("f", Var("n"), Var("acc")));

        Assert.False(Rewrite(Func("f", body)).IsTcoLoop);
    }

    [Fact]
    public void AwaitOfIfContainingSelfCall_IsNotRewritten()
    {
        // Only a *direct* self-call under the await is a back-edge. TailRecursionAnalyzer
        // mirrors this exclusion, so the drift biconditional depends on it.
        var inner = new IrNode.If(
            new IrNode.BinOp("=", Var("n"), new IrNode.IntConst(0) { Type = ZType.Int })
            {
                Type = ZType.Bool,
            },
            new IrNode.Call(Var("f"), [Var("n"), Var("acc")])
            {
                Type = new ZType.ZNamedType("Task", [ZType.Int]),
            },
            new IrNode.Call(Var("g"), []) { Type = new ZType.ZNamedType("Task", [ZType.Int]) }
        )
        {
            Type = new ZType.ZNamedType("Task", [ZType.Int]),
        };
        var body = IfBaseCase(new IrNode.Await(inner) { Type = ZType.Int });

        Assert.False(Rewrite(Func("f", body, isAsync: true)).IsTcoLoop);
    }

    #endregion

    #region RewriteModules

    /// <summary>
    ///     `loop(n, acc) = if (n == 0) acc else loop(n - 1, acc)` — the shape every stdlib
    ///     `*-loop` helper has.
    /// </summary>
    private static IrNode.FuncDef TailRecursiveLoop(string name) =>
        Func(
            name,
            new IrNode.If(
                new IrNode.BinOp("=", Var("n"), new IrNode.IntConst(0) { Type = ZType.Int })
                {
                    Type = ZType.Bool,
                },
                Var("acc"),
                Call(
                    name,
                    new IrNode.BinOp("-", Var("n"), new IrNode.IntConst(1) { Type = ZType.Int })
                    {
                        Type = ZType.Int,
                    },
                    Var("acc")
                )
            )
            {
                Type = ZType.Int,
            }
        );

    private static IrNode.FuncDef SingleFunc(
        IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)>? modules
    ) => Assert.IsType<IrNode.FuncDef>(Assert.Single(Assert.Single(modules!).Definitions));

    [Fact]
    public void RewriteModules_LoopsFunctionsInsideImportedModules()
    {
        // The regression this guards: both emitters used to lower only the main IR, so every
        // function reaching them as an imported module — which, for a package library, is
        // every function it has — stayed plain recursion on both backends.
        var modules = new[]
        {
            ("AlphaModule", (IReadOnlyList<IrNode>)[TailRecursiveLoop("alpha/loop")]),
        };

        var rewritten = new TailCallLowering().RewriteModules(modules);

        var func = SingleFunc(rewritten);
        Assert.True(func.IsTcoLoop);
        Assert.IsType<IrNode.TcoJump>(Assert.IsType<IrNode.If>(func.Body).Else);
    }

    [Fact]
    public void RewriteModules_LeavesNonTailRecursionAlone()
    {
        var body = new IrNode.BinOp("+", Call("f", Var("n"), Var("acc")), Var("acc"))
        {
            Type = ZType.Int,
        };
        var modules = new[] { ("M", (IReadOnlyList<IrNode>)[Func("f", body)]) };

        var rewritten = new TailCallLowering().RewriteModules(modules);

        Assert.False(SingleFunc(rewritten).IsTcoLoop);
    }

    [Fact]
    public void RewriteModules_IsIdempotent()
    {
        // Compilation.CompileEmit hoists imported modules and hands them to IlEmitter, which
        // lowers them itself. Nothing stops a caller from lowering first, so a second pass over
        // an already-lowered tree must find TcoJump, change nothing, and keep IsTcoLoop set.
        var modules = new[] { ("M", (IReadOnlyList<IrNode>)[TailRecursiveLoop("f")]) };
        var lowering = new TailCallLowering();

        var once = lowering.RewriteModules(modules);
        var twice = lowering.RewriteModules(once);

        var func = SingleFunc(twice);
        Assert.True(func.IsTcoLoop);
        Assert.IsType<IrNode.TcoJump>(Assert.IsType<IrNode.If>(func.Body).Else);
    }

    [Fact]
    public void RewriteModules_PassesThroughNullAndEmpty()
    {
        var lowering = new TailCallLowering();

        Assert.Null(lowering.RewriteModules(null));
        Assert.Empty(lowering.RewriteModules([])!);
    }

    #endregion
}
