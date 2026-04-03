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

        var info = AsyncStateMachineAnalyzer.Analyze(func);

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

        var info = AsyncStateMachineAnalyzer.Analyze(func);

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

        var info = AsyncStateMachineAnalyzer.Analyze(func);

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

        var info = AsyncStateMachineAnalyzer.Analyze(func);

        Assert.True(info.IsVoidReturn);
    }

    [Fact]
    public void GetAwaitResultType_ExtractsInnerType()
    {
        var resultType = AsyncStateMachineAnalyzer.GetAwaitResultType(TaskInt);
        Assert.Equal(ZType.Int, resultType);
    }

    [Fact]
    public void GetAwaitResultType_NonGenericTask_ReturnsUnit()
    {
        var taskType = new ZType.ZNamedType("Task", []);
        var resultType = AsyncStateMachineAnalyzer.GetAwaitResultType(taskType);
        Assert.Equal(ZType.Unit, resultType);
    }
}
