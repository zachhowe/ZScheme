using Xunit;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class PatternCompilerTests
{
    [Fact]
    public void WildcardPattern_NoCondition()
    {
        var match = new IrNode.Match(
                new IrNode.Var("x") { Type = ZType.Int },
                [new IrMatchArm(new IrPattern.Wildcard(), new IrNode.IntConst(42) { Type = ZType.Int })])
            { Type = ZType.Int };

        var compiler = new PatternCompiler();
        var result = compiler.Compile(match);

        // Should just be the body without any condition
        Assert.IsType<IrNode.IntConst>(result);
    }

    [Fact]
    public void VariablePattern_BindsScrutinee()
    {
        var match = new IrNode.Match(
                new IrNode.Var("x") { Type = ZType.Int },
                [
                    new IrMatchArm(
                        new IrPattern.Variable("y"),
                        new IrNode.Var("y") { Type = ZType.Int })
                ])
            { Type = ZType.Int };

        var compiler = new PatternCompiler();
        var result = compiler.Compile(match);

        // Should wrap body in a let binding
        var let = Assert.IsType<IrNode.Let>(result);
        Assert.Equal("y", let.VarName);
    }

    [Fact]
    public void LiteralPattern_GeneratesCondition()
    {
        var match = new IrNode.Match(
                new IrNode.Var("x") { Type = ZType.Int },
                [
                    new IrMatchArm(new IrPattern.Literal(1), new IrNode.StringConst("one") { Type = ZType.String }),
                    new IrMatchArm(new IrPattern.Wildcard(), new IrNode.StringConst("other") { Type = ZType.String })
                ])
            { Type = ZType.String };

        var compiler = new PatternCompiler();
        var result = compiler.Compile(match);

        // Should generate an if expression
        var @if = Assert.IsType<IrNode.If>(result);
        Assert.IsType<IrNode.BinOp>(@if.Condition);
    }

    [Fact]
    public void ConstructorPattern_GeneratesTypeTest()
    {
        var match = new IrNode.Match(
                new IrNode.Var("shape") { Type = ZType.Unit },
                [
                    new IrMatchArm(
                        new IrPattern.Constructor("Circle", [new IrPattern.Variable("r")]),
                        new IrNode.Var("r") { Type = ZType.Float }),
                    new IrMatchArm(
                        new IrPattern.Wildcard(),
                        new IrNode.FloatConst(0f) { Type = ZType.Float })
                ])
            { Type = ZType.Float };

        var compiler = new PatternCompiler();
        var result = compiler.Compile(match);

        var @if = Assert.IsType<IrNode.If>(result);
        Assert.IsType<IrNode.TypeTest>(@if.Condition);
    }

    [Fact]
    public void MultipleLiteralPatterns_ChainIntoNestedIfs()
    {
        var match = new IrNode.Match(
                new IrNode.Var("x") { Type = ZType.Int },
                [
                    new IrMatchArm(new IrPattern.Literal(1), new IrNode.StringConst("one") { Type = ZType.String }),
                    new IrMatchArm(new IrPattern.Literal(2), new IrNode.StringConst("two") { Type = ZType.String }),
                    new IrMatchArm(new IrPattern.Wildcard(), new IrNode.StringConst("other") { Type = ZType.String })
                ])
            { Type = ZType.String };

        var compiler = new PatternCompiler();
        var result = compiler.Compile(match);

        // First literal generates an if, else branch contains another if for the second literal
        var outerIf = Assert.IsType<IrNode.If>(result);
        Assert.IsType<IrNode.BinOp>(outerIf.Condition);
        var innerIf = Assert.IsType<IrNode.If>(outerIf.Else);
        Assert.IsType<IrNode.BinOp>(innerIf.Condition);
    }

    [Fact]
    public void ConstructorWithMultipleFields_BindsAllFields()
    {
        var match = new IrNode.Match(
                new IrNode.Var("p") { Type = ZType.Unit },
                [
                    new IrMatchArm(
                        new IrPattern.Constructor("Point", [
                            new IrPattern.Variable("x"),
                            new IrPattern.Variable("y")
                        ]),
                        new IrNode.BinOp("+",
                            new IrNode.Var("x") { Type = ZType.Int },
                            new IrNode.Var("y") { Type = ZType.Int }) { Type = ZType.Int }),
                    new IrMatchArm(
                        new IrPattern.Wildcard(),
                        new IrNode.IntConst(0) { Type = ZType.Int })
                ])
            { Type = ZType.Int };

        var compiler = new PatternCompiler();
        var result = compiler.Compile(match);

        var @if = Assert.IsType<IrNode.If>(result);
        Assert.IsType<IrNode.TypeTest>(@if.Condition);
    }

    [Fact]
    public void NestedConstructorPatterns()
    {
        var match = new IrNode.Match(
                new IrNode.Var("s") { Type = ZType.Unit },
                [
                    new IrMatchArm(
                        new IrPattern.Constructor("Some", [
                            new IrPattern.Constructor("Ok", [new IrPattern.Variable("v")])
                        ]),
                        new IrNode.Var("v") { Type = ZType.Int }),
                    new IrMatchArm(
                        new IrPattern.Wildcard(),
                        new IrNode.IntConst(0) { Type = ZType.Int })
                ])
            { Type = ZType.Int };

        var compiler = new PatternCompiler();
        var result = compiler.Compile(match);

        Assert.IsType<IrNode.If>(result);
    }
}
