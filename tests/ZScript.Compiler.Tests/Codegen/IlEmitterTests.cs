namespace ZScript.Compiler.Tests.Codegen;

using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;
using Xunit;

public class IlEmitterTests
{
    [Fact]
    public void EmitSimpleAddFunction()
    {
        var func = new IrNode.FuncDef(
            "Add",
            [new IrParam("x", ZType.Int), new IrParam("y", ZType.Int)],
            ZType.Int,
            new IrNode.BinOp("+",
                new IrNode.Var("x") { Type = ZType.Int },
                new IrNode.Var("y") { Type = ZType.Int })
            { Type = ZType.Int },
            false)
        { Type = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitIfExpression()
    {
        var body = new IrNode.If(
            new IrNode.BinOp("=",
                new IrNode.Var("x") { Type = ZType.Int },
                new IrNode.IntConst(0) { Type = ZType.Int })
            { Type = ZType.Bool },
            new IrNode.IntConst(1) { Type = ZType.Int },
            new IrNode.IntConst(0) { Type = ZType.Int })
        { Type = ZType.Int };

        var func = new IrNode.FuncDef("IsZero",
            [new IrParam("x", ZType.Int)],
            ZType.Int, body, false)
        { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void EmitStringConstant()
    {
        var func = new IrNode.FuncDef("Greet",
            [],
            ZType.String,
            new IrNode.StringConst("hello") { Type = ZType.String },
            false)
        { Type = new ZType.ZFuncType([], ZType.String) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }
}
