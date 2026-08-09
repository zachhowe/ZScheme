using Xunit;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class TailCallLoweringTests
{
    private static IrNode.FuncDef Rewrite(IrNode.FuncDef func, bool includeAsync = true) =>
        (IrNode.FuncDef)new TailCallLowering(includeAsync).Rewrite(func);

    private static IrNode.Var Var(string name) => new(name) { Type = ZType.Int };

    private static IrNode.Call Call(string name, params IrNode[] args) =>
        new(Var(name), args) { Type = ZType.Int };

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

    [Fact]
    public void ExcludesAsyncFunction_WhenIncludeAsyncFalse()
    {
        var body = new IrNode.If(
            new IrNode.BinOp("=", Var("n"), new IrNode.IntConst(0) { Type = ZType.Int })
            {
                Type = ZType.Bool,
            },
            Var("acc"),
            Call("f", Var("n"), Var("acc"))
        )
        {
            Type = ZType.Int,
        };

        // IL backend (includeAsync: false): async self-recursion is left as a plain call.
        var il = Rewrite(Func("f", body, isAsync: true), includeAsync: false);
        Assert.False(il.IsTcoLoop);

        // C# backend (includeAsync: true): it is rewritten to a loop.
        var cs = Rewrite(Func("f", body, isAsync: true), includeAsync: true);
        Assert.True(cs.IsTcoLoop);
    }

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

        var rewritten = new TailCallLowering(includeAsync: true).RewriteModules(modules);

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

        var rewritten = new TailCallLowering(includeAsync: true).RewriteModules(modules);

        Assert.False(SingleFunc(rewritten).IsTcoLoop);
    }

    [Fact]
    public void RewriteModules_IsIdempotent()
    {
        // Compilation.CompileEmit hoists imported modules and hands them to IlEmitter, which
        // lowers them itself. Nothing stops a caller from lowering first, so a second pass over
        // an already-lowered tree must find TcoJump, change nothing, and keep IsTcoLoop set.
        var modules = new[] { ("M", (IReadOnlyList<IrNode>)[TailRecursiveLoop("f")]) };
        var lowering = new TailCallLowering(includeAsync: true);

        var once = lowering.RewriteModules(modules);
        var twice = lowering.RewriteModules(once);

        var func = SingleFunc(twice);
        Assert.True(func.IsTcoLoop);
        Assert.IsType<IrNode.TcoJump>(Assert.IsType<IrNode.If>(func.Body).Else);
    }

    [Fact]
    public void RewriteModules_PassesThroughNullAndEmpty()
    {
        var lowering = new TailCallLowering(includeAsync: true);

        Assert.Null(lowering.RewriteModules(null));
        Assert.Empty(lowering.RewriteModules([])!);
    }

    #endregion
}
