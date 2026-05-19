using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

public class AsyncStateMachineAnalyzerTests
{
    private static readonly ZType TaskInt = new ZType.ZNamedType("Task", [ZType.Int]);

    [Fact]
    public void ContainsAwait_ReturnsTrueForAwaitNode()
    {
        var node = new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int };
        Assert.True(AsyncStateMachineAnalyzer.ContainsAwait(node));
    }

    [Fact]
    public void ContainsAwait_ReturnsFalseForNonAwaitNode()
    {
        var node = new IrNode.IntConst(42) { Type = ZType.Int };
        Assert.False(AsyncStateMachineAnalyzer.ContainsAwait(node));
    }

    [Fact]
    public void ContainsAwait_FindsAwaitInsideLet()
    {
        var node = new IrNode.Let("x",
            new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int },
            new IrNode.Var("x") { Type = ZType.Int }) { Type = ZType.Int };
        Assert.True(AsyncStateMachineAnalyzer.ContainsAwait(node));
    }

    [Fact]
    public void ContainsAwait_FindsAwaitInsideIf()
    {
        var node = new IrNode.If(
            new IrNode.BoolConst(true) { Type = ZType.Bool },
            new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int },
            new IrNode.IntConst(0) { Type = ZType.Int }) { Type = ZType.Int };
        Assert.True(AsyncStateMachineAnalyzer.ContainsAwait(node));
    }

    [Fact]
    public void Analyze_SingleAwait_FindsOneAwaitPoint()
    {
        var func = new IrNode.FuncDef("test",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.Let("result",
                    new IrNode.Await(new IrNode.Var("x") { Type = TaskInt }) { Type = ZType.Int },
                    new IrNode.Var("result") { Type = ZType.Int }) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskInt) };

        var info = AsyncStateMachineAnalyzer.Analyze(func, new TypeAliasRegistry());

        Assert.Single(info.AwaitPoints);
        Assert.Equal(0, info.AwaitPoints[0].StateNumber);
        Assert.Single(info.HoistedLocals);
        Assert.Equal("result", info.HoistedLocals[0].Name);
        Assert.False(info.IsVoidReturn);
    }

    [Fact]
    public void Analyze_TwoChainedAwaits_FindsTwoAwaitPoints()
    {
        var func = new IrNode.FuncDef("test",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.Let("a",
                        new IrNode.Await(new IrNode.Var("x") { Type = TaskInt }) { Type = ZType.Int },
                        new IrNode.Let("b",
                                new IrNode.Await(new IrNode.Var("a") { Type = TaskInt }) { Type = ZType.Int },
                                new IrNode.BinOp("+",
                                    new IrNode.Var("a") { Type = ZType.Int },
                                    new IrNode.Var("b") { Type = ZType.Int }) { Type = ZType.Int })
                            { Type = ZType.Int })
                    { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskInt) };

        var info = AsyncStateMachineAnalyzer.Analyze(func, new TypeAliasRegistry());

        Assert.Equal(2, info.AwaitPoints.Count);
        Assert.Equal(0, info.AwaitPoints[0].StateNumber);
        Assert.Equal(1, info.AwaitPoints[1].StateNumber);
        Assert.Equal(2, info.HoistedLocals.Count);
    }

    [Fact]
    public void Analyze_VoidReturn_SetsIsVoidReturnTrue()
    {
        var func = new IrNode.FuncDef("test", [], ZType.Unit,
                new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], new ZType.ZNamedType("Task", [])) };

        var info = AsyncStateMachineAnalyzer.Analyze(func, new TypeAliasRegistry());

        Assert.True(info.IsVoidReturn);
    }

    [Fact]
    public void Analyze_BareTaskReturnType_SetsIsVoidReturnTrue()
    {
        var taskType = new ZType.ZNamedType("Task", []);
        var func = new IrNode.FuncDef("test", [], taskType,
                new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], taskType) };

        var info = AsyncStateMachineAnalyzer.Analyze(func, new TypeAliasRegistry());

        Assert.True(info.IsVoidReturn);
    }

    [Fact]
    public void GetAwaitResultType_ExtractsInnerType()
    {
        var resultType = AsyncStateMachineAnalyzer.GetAwaitResultType(TaskInt, new TypeAliasRegistry());
        Assert.Equal(ZType.Int, resultType);
    }

    [Fact]
    public void GetAwaitResultType_NonGenericTask_ReturnsUnit()
    {
        var taskType = new ZType.ZNamedType("Task", []);
        var resultType = AsyncStateMachineAnalyzer.GetAwaitResultType(taskType, new TypeAliasRegistry());
        Assert.Equal(ZType.Unit, resultType);
    }

    [Fact]
    public void ContainsAwait_FindsAwaitInsideWithHandlersBody()
    {
        var node = new IrNode.WithHandlers(
            new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int },
            [new IrHandlerClause("System.Exception", "_", new IrNode.IntConst(0) { Type = ZType.Int })])
        { Type = ZType.Int };
        Assert.True(AsyncStateMachineAnalyzer.ContainsAwait(node));
    }

    [Fact]
    public void ContainsAwait_FindsAwaitInsideWithHandlersHandlerBody()
    {
        var node = new IrNode.WithHandlers(
            new IrNode.IntConst(0) { Type = ZType.Int },
            [new IrHandlerClause("System.Exception", "_",
                new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int })])
        { Type = ZType.Int };
        Assert.True(AsyncStateMachineAnalyzer.ContainsAwait(node));
    }

    [Fact]
    public void ContainsAwait_FindsAwaitInsideSeq()
    {
        var node = new IrNode.Seq([
            new IrNode.IntConst(0) { Type = ZType.Int },
            new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int }
        ])
        { Type = ZType.Int };
        Assert.True(AsyncStateMachineAnalyzer.ContainsAwait(node));
    }

    [Fact]
    public void ContainsAwait_FindsAwaitInsideThrow()
    {
        var node = new IrNode.Throw(
            new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int })
        { Type = ZType.Int };
        Assert.True(AsyncStateMachineAnalyzer.ContainsAwait(node));
    }

    [Fact]
    public void ContainsAwait_FindsAwaitInsideBinOp()
    {
        var node = new IrNode.BinOp("+",
            new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int },
            new IrNode.IntConst(2) { Type = ZType.Int })
        { Type = ZType.Int };
        Assert.True(AsyncStateMachineAnalyzer.ContainsAwait(node));
    }

    [Fact]
    public void Analyze_AwaitInsideWithHandlersBody_IsCounted()
    {
        // Regression: AsyncStateMachineAnalyzer didn't recurse into WithHandlers, so awaits
        // inside try-bodies were skipped during analysis but still emitted by EmitMoveNextAwait,
        // causing a KeyNotFoundException when looking up the missing awaiter field.
        var func = new IrNode.FuncDef("test", [], ZType.Int,
                new IrNode.WithHandlers(
                    new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int },
                    [new IrHandlerClause("System.Exception", "_",
                        new IrNode.IntConst(0) { Type = ZType.Int })])
                { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskInt) };

        var info = AsyncStateMachineAnalyzer.Analyze(func, new TypeAliasRegistry());

        Assert.Single(info.AwaitPoints);
        Assert.Equal(0, info.AwaitPoints[0].StateNumber);
    }

    [Fact]
    public void Analyze_AwaitInsideWithHandlersHandlerBody_IsCounted()
    {
        var func = new IrNode.FuncDef("test", [], ZType.Int,
                new IrNode.WithHandlers(
                    new IrNode.IntConst(0) { Type = ZType.Int },
                    [new IrHandlerClause("System.Exception", "_",
                        new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int })])
                { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskInt) };

        var info = AsyncStateMachineAnalyzer.Analyze(func, new TypeAliasRegistry());

        Assert.Single(info.AwaitPoints);
    }

    [Fact]
    public void Analyze_AwaitInsideIfThenWithHandlers_AndAwaitInElse_BothCounted()
    {
        // Regression: when an `if` had `(with-handlers ... (await ...))` in one branch and
        // `(await ...)` in the other, the analyzer counted only the second await but the
        // emitter visited both, so the second emit's stateNum exceeded AwaiterFields.Count.
        var func = new IrNode.FuncDef("test", [], ZType.Int,
                new IrNode.If(
                    new IrNode.BoolConst(true) { Type = ZType.Bool },
                    new IrNode.WithHandlers(
                        new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int },
                        [new IrHandlerClause("System.Exception", "_",
                            new IrNode.IntConst(0) { Type = ZType.Int })])
                    { Type = ZType.Int },
                    new IrNode.Await(new IrNode.IntConst(2) { Type = TaskInt }) { Type = ZType.Int })
                { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskInt) };

        var info = AsyncStateMachineAnalyzer.Analyze(func, new TypeAliasRegistry());

        Assert.Equal(2, info.AwaitPoints.Count);
        Assert.Equal(0, info.AwaitPoints[0].StateNumber);
        Assert.Equal(1, info.AwaitPoints[1].StateNumber);
    }

    [Fact]
    public void Analyze_AwaitInsideSeq_IsCounted()
    {
        var func = new IrNode.FuncDef("test", [], ZType.Int,
                new IrNode.Seq([
                    new IrNode.IntConst(0) { Type = ZType.Int },
                    new IrNode.Await(new IrNode.IntConst(1) { Type = TaskInt }) { Type = ZType.Int }
                ])
                { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskInt) };

        var info = AsyncStateMachineAnalyzer.Analyze(func, new TypeAliasRegistry());

        Assert.Single(info.AwaitPoints);
    }

    [Fact]
    public void Analyze_NestedAwaitInsideAwaitedExpr_BothCounted()
    {
        // Regression: when an outer (await X) contained a nested (await Y) inside X,
        // the analyzer's Await case did not recurse into awaitNode.Expr. The outer
        // Await was counted but the nested one was not, so AwaiterFields had only
        // one entry. The IL emitter still walked into the inner await (because
        // EmitMoveNextAwait emits Expr before consuming itself), and the lookup
        // for the second state number threw KeyNotFoundException.
        //
        // The IL emitter pushes args before the call, so the nested await is
        // emitted first and gets state 0; the outer await gets state 1. Both
        // must appear in info.AwaitPoints.
        var taskCall = new IrNode.Call(
                new IrNode.Var("g") { Type = new ZType.ZFuncType([ZType.Int], TaskInt) },
                [new IrNode.Await(new IrNode.Call(
                            new IrNode.Var("g") { Type = new ZType.ZFuncType([ZType.Int], TaskInt) },
                            [new IrNode.IntConst(1) { Type = ZType.Int }])
                        { Type = TaskInt })
                    { Type = ZType.Int }])
            { Type = TaskInt };

        var func = new IrNode.FuncDef("f", [], ZType.Int,
                new IrNode.Await(taskCall) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskInt) };

        var info = AsyncStateMachineAnalyzer.Analyze(func, new TypeAliasRegistry());

        Assert.Equal(2, info.AwaitPoints.Count);
        Assert.Equal(0, info.AwaitPoints[0].StateNumber);
        Assert.Equal(1, info.AwaitPoints[1].StateNumber);
    }

    [Fact]
    public void Analyze_DeeplyNestedAwaits_AllCounted()
    {
        // (await (g (await (g (await (g 1))))))
        // Three awaits, each nested inside the awaited expression of the next.
        var fnTy = new ZType.ZFuncType([ZType.Int], TaskInt);
        IrNode innermost = new IrNode.Await(
                new IrNode.Call(
                    new IrNode.Var("g") { Type = fnTy },
                    [new IrNode.IntConst(1) { Type = ZType.Int }]) { Type = TaskInt })
            { Type = ZType.Int };
        var middle = new IrNode.Await(
                new IrNode.Call(
                    new IrNode.Var("g") { Type = fnTy },
                    [innermost]) { Type = TaskInt })
            { Type = ZType.Int };
        var outer = new IrNode.Await(
                new IrNode.Call(
                    new IrNode.Var("g") { Type = fnTy },
                    [middle]) { Type = TaskInt })
            { Type = ZType.Int };

        var func = new IrNode.FuncDef("f", [], ZType.Int, outer, false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskInt) };

        var info = AsyncStateMachineAnalyzer.Analyze(func, new TypeAliasRegistry());

        Assert.Equal(3, info.AwaitPoints.Count);
        Assert.Equal(0, info.AwaitPoints[0].StateNumber);
        Assert.Equal(1, info.AwaitPoints[1].StateNumber);
        Assert.Equal(2, info.AwaitPoints[2].StateNumber);
    }
}
