using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class WithHandlersHoisterTests
{
    private static IrNode.Var V(string name) => new(name) { Type = ZType.Int };

    private static IrNode.IntConst Int(int value) => new(value) { Type = ZType.Int };

    private static IrNode.WithHandlers Wh() =>
        new(Int(1), [new IrHandlerClause("System.Exception", "e", Int(2))]) { Type = ZType.Int };

    [Fact]
    public void ContainsWithHandlersDetectsHandlerNodes()
    {
        Assert.True(WithHandlersHoister.ContainsWithHandlers(Wh()));
        Assert.False(WithHandlersHoister.ContainsWithHandlers(Int(1)));
        Assert.False(WithHandlersHoister.ContainsWithHandlers(V("x")));
    }

    [Fact]
    public void ContainsWithHandlersTreatsUseAsBarrier()
    {
        // 'use' lowers to an IL try/finally, so it is a hoist barrier like with-handlers.
        var use = new IrNode.Use("r", Int(1), Int(2)) { Type = ZType.Int };
        Assert.True(WithHandlersHoister.ContainsWithHandlers(use));
    }

    [Fact]
    public void ContainsWithHandlersWalksIntoCompoundNodes()
    {
        var inBinOp = new IrNode.BinOp("+", Int(1), Wh()) { Type = ZType.Int };
        Assert.True(WithHandlersHoister.ContainsWithHandlers(inBinOp));

        var inCallArg = new IrNode.Call(V("f"), [Wh()]) { Type = ZType.Int };
        Assert.True(WithHandlersHoister.ContainsWithHandlers(inCallArg));

        var inMatchArm = new IrNode.Match(V("x"), [new IrMatchArm(new IrPattern.Wildcard(), Wh())])
        {
            Type = ZType.Int,
        };
        Assert.True(WithHandlersHoister.ContainsWithHandlers(inMatchArm));
    }

    [Fact]
    public void HandlerFreeTreeIsNotAnfd()
    {
        var binop = new IrNode.BinOp("+", Int(1), Int(2)) { Type = ZType.Int };

        var result = new WithHandlersHoister().Hoist(binop);

        var rebuilt = Assert.IsType<IrNode.BinOp>(result);
        Assert.Same(binop.Left, rebuilt.Left);
        Assert.Same(binop.Right, rebuilt.Right);
    }

    [Fact]
    public void ArithmeticBinOpWithHandlerOperandBecomesLetSpine()
    {
        var binop = new IrNode.BinOp("+", Wh(), Int(2)) { Type = ZType.Int };

        var result = new WithHandlersHoister().Hoist(binop);

        var outer = Assert.IsType<IrNode.Let>(result);
        Assert.Equal("__wh_hoist_0", outer.VarName);
        Assert.IsType<IrNode.WithHandlers>(outer.Value);

        var inner = Assert.IsType<IrNode.Let>(outer.Body);
        Assert.Equal("__wh_hoist_1", inner.VarName);

        var rebuilt = Assert.IsType<IrNode.BinOp>(inner.Body);
        Assert.Equal("__wh_hoist_0", Assert.IsType<IrNode.Var>(rebuilt.Left).Name);
        Assert.Equal("__wh_hoist_1", Assert.IsType<IrNode.Var>(rebuilt.Right).Name);
    }

    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    public void ShortCircuitOperatorsAreNeverAnfd(string op)
    {
        // A-normalizing 'and'/'or' would evaluate the right operand unconditionally
        // via the Let, breaking short-circuit semantics.
        var wh = new IrNode.WithHandlers(
            new IrNode.BoolConst(true) { Type = ZType.Bool },
            [
                new IrHandlerClause(
                    "System.Exception",
                    "e",
                    new IrNode.BoolConst(false) { Type = ZType.Bool }
                ),
            ]
        )
        {
            Type = ZType.Bool,
        };
        var binop = new IrNode.BinOp(op, new IrNode.BoolConst(true) { Type = ZType.Bool }, wh)
        {
            Type = ZType.Bool,
        };

        var result = new WithHandlersHoister().Hoist(binop);

        var rebuilt = Assert.IsType<IrNode.BinOp>(result);
        Assert.IsType<IrNode.WithHandlers>(rebuilt.Right);
    }

    [Fact]
    public void AwaitOperandContainingHandlersIsAnfd()
    {
        // The mirror-image of AwaitHoister: an Await whose operand contains a
        // with-handlers must evaluate the operand at stack depth 0 first.
        var aw = new IrNode.Await(Wh()) { Type = ZType.Int };

        var result = new WithHandlersHoister().Hoist(aw);

        var let = Assert.IsType<IrNode.Let>(result);
        Assert.Equal("__wh_hoist_0", let.VarName);
        Assert.IsType<IrNode.WithHandlers>(let.Value);
        var rebuilt = Assert.IsType<IrNode.Await>(let.Body);
        Assert.IsType<IrNode.Var>(rebuilt.Expr);
    }

    [Fact]
    public void CallWithVarCalleeKeepsCalleeAndAnfsArgs()
    {
        var fn = V("f");
        var call = new IrNode.Call(fn, [Wh(), Int(2)]) { Type = ZType.Int };

        var result = new WithHandlersHoister().Hoist(call);

        var outer = Assert.IsType<IrNode.Let>(result);
        var inner = Assert.IsType<IrNode.Let>(outer.Body);
        var rebuilt = Assert.IsType<IrNode.Call>(inner.Body);
        Assert.Same(fn, rebuilt.Function);
        Assert.All(rebuilt.Args, a => Assert.IsType<IrNode.Var>(a));
    }

    [Fact]
    public void CallWithNonVarCalleeHoistsCalleeToo()
    {
        var call = new IrNode.Call(Wh(), [Int(1)]) { Type = ZType.Int };

        var result = new WithHandlersHoister().Hoist(call);

        var outer = Assert.IsType<IrNode.Let>(result);
        Assert.IsType<IrNode.WithHandlers>(outer.Value);
        var inner = Assert.IsType<IrNode.Let>(outer.Body);
        var rebuilt = Assert.IsType<IrNode.Call>(inner.Body);
        Assert.Equal("__wh_hoist_0", Assert.IsType<IrNode.Var>(rebuilt.Function).Name);
    }

    [Fact]
    public void UseNodeIsReconstructedNotAnfdAtItsOwnPosition()
    {
        var use = new IrNode.Use("r", Wh(), V("r")) { Type = ZType.Int };

        var result = new WithHandlersHoister().Hoist(use);

        var rebuilt = Assert.IsType<IrNode.Use>(result);
        Assert.Equal("r", rebuilt.VarName);
        Assert.IsType<IrNode.WithHandlers>(rebuilt.Value);
    }

    [Fact]
    public void LetEmitNameSurvivesRewriting()
    {
        // Both hoisters run unconditionally at the IL emitter's entry, so a module-level
        // `let` renamed by EmitNameResolver passes through here even in a program with no
        // with-handlers at all. Rebuilding it positionally dropped the rename.
        var let = new IrNode.Let("this-value", Int(1), Wh(), ZType.Int, EmitName: "ThisValue_fn")
        {
            Type = ZType.Int,
        };

        var result = new WithHandlersHoister().Hoist(let);

        var rebuilt = Assert.IsType<IrNode.Let>(result);
        Assert.Equal("ThisValue_fn", rebuilt.EmitName);
    }

    [Fact]
    public void OriginalSpanIsRestoredOnRewrittenRoot()
    {
        var span = new SourceSpan("test.zs", 5, 1, 9);
        var binop = new IrNode.BinOp("+", Wh(), Int(2)) { Type = ZType.Int, Span = span };

        var result = new WithHandlersHoister().Hoist(binop);

        Assert.Equal(span, result.Span);
    }
}
