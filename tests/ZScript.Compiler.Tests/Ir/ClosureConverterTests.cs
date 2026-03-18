namespace ZScript.Compiler.Tests.Ir;

using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;
using Xunit;

public class ClosureConverterTests
{
    [Fact]
    public void NoFreeVars_RemainsUnchanged()
    {
        var func = new IrNode.FuncDef("add",
            [new IrParam("x", ZType.Int), new IrParam("y", ZType.Int)],
            ZType.Int,
            new IrNode.BinOp("+",
                new IrNode.Var("x") { Type = ZType.Int },
                new IrNode.Var("y") { Type = ZType.Int }) { Type = ZType.Int },
            false) { Type = ZType.Int };

        var converter = new ClosureConverter();
        var result = converter.Convert(func);

        Assert.IsType<IrNode.FuncDef>(result);
        Assert.Empty(converter.LiftedFunctions);
    }

    [Fact]
    public void FreeVars_ReturnsClosureNode()
    {
        // Lambda captures "z" which is not a parameter
        var func = new IrNode.FuncDef("f",
            [new IrParam("x", ZType.Int)],
            ZType.Int,
            new IrNode.BinOp("+",
                new IrNode.Var("x") { Type = ZType.Int },
                new IrNode.Var("z") { Type = ZType.Int }) { Type = ZType.Int },
            false) { Type = ZType.Int };

        var converter = new ClosureConverter();
        var result = converter.Convert(func);

        var closure = Assert.IsType<IrNode.Closure>(result);
        Assert.Contains("z", closure.CapturedValues.OfType<IrNode.Var>().Select(v => v.Name));
        Assert.Single(converter.LiftedFunctions);
    }

    [Fact]
    public void LiftedFunction_HasCaptureParamsPrepended()
    {
        var func = new IrNode.FuncDef("f",
            [new IrParam("x", ZType.Int)],
            ZType.Int,
            new IrNode.BinOp("+",
                new IrNode.Var("x") { Type = ZType.Int },
                new IrNode.Var("y") { Type = ZType.Int }) { Type = ZType.Int },
            false) { Type = ZType.Int };

        var converter = new ClosureConverter();
        converter.Convert(func);

        var lifted = Assert.Single(converter.LiftedFunctions);
        // First param should be the capture "y", second should be original "x"
        Assert.Equal(2, lifted.Params.Count);
        Assert.Equal("y", lifted.Params[0].Name);
        Assert.Equal("x", lifted.Params[1].Name);
    }

    [Fact]
    public void NestedFunctions_WithCaptures()
    {
        // outer captures "a", inner captures "b"
        var inner = new IrNode.FuncDef("inner",
            [new IrParam("p", ZType.Int)],
            ZType.Int,
            new IrNode.BinOp("+",
                new IrNode.Var("p") { Type = ZType.Int },
                new IrNode.Var("b") { Type = ZType.Int }) { Type = ZType.Int },
            false) { Type = ZType.Int };

        var outer = new IrNode.FuncDef("outer",
            [new IrParam("q", ZType.Int)],
            ZType.Int,
            new IrNode.Let("tmp", inner,
                new IrNode.BinOp("+",
                    new IrNode.Var("q") { Type = ZType.Int },
                    new IrNode.Var("a") { Type = ZType.Int }) { Type = ZType.Int })
            { Type = ZType.Int },
            false) { Type = ZType.Int };

        var converter = new ClosureConverter();
        converter.Convert(outer);

        // Both inner and outer should be lifted
        Assert.Equal(2, converter.LiftedFunctions.Count);
    }

    [Fact]
    public void LetBinding_BoundVarNotFree()
    {
        // (let [y 5] (+ x y)) — y is bound by let, only x is free
        var func = new IrNode.FuncDef("f",
            [],
            ZType.Int,
            new IrNode.Let("y",
                new IrNode.IntConst(5) { Type = ZType.Int },
                new IrNode.BinOp("+",
                    new IrNode.Var("x") { Type = ZType.Int },
                    new IrNode.Var("y") { Type = ZType.Int }) { Type = ZType.Int })
            { Type = ZType.Int },
            false) { Type = ZType.Int };

        var converter = new ClosureConverter();
        var result = converter.Convert(func);

        var closure = Assert.IsType<IrNode.Closure>(result);
        var captured = closure.CapturedValues.OfType<IrNode.Var>().Select(v => v.Name).ToList();
        Assert.Contains("x", captured);
        Assert.DoesNotContain("y", captured);
    }

    [Fact]
    public void FreeVarDetection_ThroughIfBranches()
    {
        var func = new IrNode.FuncDef("f",
            [new IrParam("x", ZType.Int)],
            ZType.Int,
            new IrNode.If(
                new IrNode.Var("x") { Type = ZType.Bool },
                new IrNode.Var("a") { Type = ZType.Int },
                new IrNode.Var("b") { Type = ZType.Int })
            { Type = ZType.Int },
            false) { Type = ZType.Int };

        var converter = new ClosureConverter();
        var result = converter.Convert(func);

        var closure = Assert.IsType<IrNode.Closure>(result);
        var captured = closure.CapturedValues.OfType<IrNode.Var>().Select(v => v.Name).ToHashSet();
        Assert.Contains("a", captured);
        Assert.Contains("b", captured);
    }

    [Fact]
    public void FreeVarDetection_ThroughCallArgs()
    {
        var func = new IrNode.FuncDef("f",
            [new IrParam("x", ZType.Int)],
            ZType.Int,
            new IrNode.Call(
                new IrNode.Var("g") { Type = ZType.Int },
                [new IrNode.Var("x") { Type = ZType.Int },
                 new IrNode.Var("free") { Type = ZType.Int }])
            { Type = ZType.Int },
            false) { Type = ZType.Int };

        var converter = new ClosureConverter();
        var result = converter.Convert(func);

        var closure = Assert.IsType<IrNode.Closure>(result);
        var captured = closure.CapturedValues.OfType<IrNode.Var>().Select(v => v.Name).ToHashSet();
        Assert.Contains("g", captured);
        Assert.Contains("free", captured);
        Assert.DoesNotContain("x", captured);
    }

    [Fact]
    public void MultipleClosures_GetUniqueNames()
    {
        var func1 = new IrNode.FuncDef("f1",
            [],
            ZType.Int,
            new IrNode.Var("a") { Type = ZType.Int },
            false) { Type = ZType.Int };

        var func2 = new IrNode.FuncDef("f2",
            [],
            ZType.Int,
            new IrNode.Var("b") { Type = ZType.Int },
            false) { Type = ZType.Int };

        var seq = new IrNode.Seq([func1, func2]) { Type = ZType.Unit };

        var converter = new ClosureConverter();
        converter.Convert(seq);

        Assert.Equal(2, converter.LiftedFunctions.Count);
        Assert.NotEqual(converter.LiftedFunctions[0].Name, converter.LiftedFunctions[1].Name);
    }
}
