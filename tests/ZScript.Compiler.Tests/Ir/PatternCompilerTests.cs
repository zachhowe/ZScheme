namespace ZScript.Compiler.Tests.Ir;

using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;
using Xunit;

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
            [new IrMatchArm(
                new IrPattern.Variable("y"),
                new IrNode.Var("y") { Type = ZType.Int })])
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
}
