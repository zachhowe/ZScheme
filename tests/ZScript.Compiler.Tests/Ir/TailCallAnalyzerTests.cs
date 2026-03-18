namespace ZScript.Compiler.Tests.Ir;

using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;
using Xunit;

public class TailCallAnalyzerTests
{
    [Fact]
    public void MarksTailCall_InDirectRecursion()
    {
        // factorial(n, acc) = if (n == 0) acc else factorial(n-1, n*acc)
        var recursiveCall = new IrNode.Call(
            new IrNode.Var("factorial") { Type = ZType.Int },
            [
                new IrNode.BinOp("-", new IrNode.Var("n") { Type = ZType.Int },
                    new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
                new IrNode.BinOp("*", new IrNode.Var("n") { Type = ZType.Int },
                    new IrNode.Var("acc") { Type = ZType.Int }) { Type = ZType.Int }
            ]) { Type = ZType.Int };

        var body = new IrNode.If(
            new IrNode.BinOp("=", new IrNode.Var("n") { Type = ZType.Int },
                new IrNode.IntConst(0) { Type = ZType.Int }) { Type = ZType.Bool },
            new IrNode.Var("acc") { Type = ZType.Int },
            recursiveCall)
        { Type = ZType.Int };

        var func = new IrNode.FuncDef("factorial",
            [new IrParam("n", ZType.Int), new IrParam("acc", ZType.Int)],
            ZType.Int, body, true) { Type = ZType.Int };

        var analyzer = new TailCallAnalyzer();
        analyzer.Analyze(func);

        Assert.True(recursiveCall.IsTailCall);
    }

    [Fact]
    public void DoesNotMarkNonTailCall()
    {
        // bad(n) = bad(n-1) + 1  (not tail position)
        var call = new IrNode.Call(
            new IrNode.Var("bad") { Type = ZType.Int },
            [new IrNode.BinOp("-", new IrNode.Var("n") { Type = ZType.Int },
                new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int }])
        { Type = ZType.Int };

        var body = new IrNode.BinOp("+", call,
            new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int };

        var func = new IrNode.FuncDef("bad",
            [new IrParam("n", ZType.Int)],
            ZType.Int, body, true) { Type = ZType.Int };

        var analyzer = new TailCallAnalyzer();
        analyzer.Analyze(func);

        Assert.False(call.IsTailCall);
    }

    [Fact]
    public void MarksTailCallInLetBody()
    {
        var call = new IrNode.Call(
            new IrNode.Var("f") { Type = ZType.Int },
            [new IrNode.Var("y") { Type = ZType.Int }])
        { Type = ZType.Int };

        var body = new IrNode.Let("y",
            new IrNode.IntConst(5) { Type = ZType.Int },
            call) { Type = ZType.Int };

        var func = new IrNode.FuncDef("f",
            [new IrParam("x", ZType.Int)],
            ZType.Int, body, true) { Type = ZType.Int };

        var analyzer = new TailCallAnalyzer();
        analyzer.Analyze(func);

        Assert.True(call.IsTailCall);
    }
}
