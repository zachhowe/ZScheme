using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class AwaitHoisterTests
{
    private static readonly ZType TaskInt = new ZType.ZNamedType("Task", [ZType.Int]);

    private static IrNode.Var V(string name) => new(name) { Type = ZType.Int };

    private static IrNode.IntConst Int(int value) => new(value) { Type = ZType.Int };

    private static IrNode.Await Await(IrNode expr) => new(expr) { Type = ZType.Int };

    private static IrNode.Await AwaitTask(string taskVar) =>
        Await(new IrNode.Var(taskVar) { Type = TaskInt });

    [Fact]
    public void AwaitFreeTreeIsNotAnfd()
    {
        var binop = new IrNode.BinOp("+", Int(1), Int(2)) { Type = ZType.Int };

        var result = new AwaitHoister().Hoist(binop);

        var rebuilt = Assert.IsType<IrNode.BinOp>(result);
        Assert.Same(binop.Left, rebuilt.Left);
        Assert.Same(binop.Right, rebuilt.Right);
    }

    [Fact]
    public void BinOpWithAwaitOperandBecomesLetSpine()
    {
        var binop = new IrNode.BinOp("+", Int(1), AwaitTask("t")) { Type = ZType.Int };

        var result = new AwaitHoister().Hoist(binop);

        var outer = Assert.IsType<IrNode.Let>(result);
        Assert.Equal("__await_hoist_0", outer.VarName);
        Assert.IsType<IrNode.IntConst>(outer.Value);

        var inner = Assert.IsType<IrNode.Let>(outer.Body);
        Assert.Equal("__await_hoist_1", inner.VarName);
        Assert.IsType<IrNode.Await>(inner.Value);

        var rebuilt = Assert.IsType<IrNode.BinOp>(inner.Body);
        var left = Assert.IsType<IrNode.Var>(rebuilt.Left);
        var right = Assert.IsType<IrNode.Var>(rebuilt.Right);
        Assert.Equal("__await_hoist_0", left.Name);
        Assert.Equal("__await_hoist_1", right.Name);
        // Hoist vars carry the hoisted operand's type.
        Assert.Equal(ZType.Int, left.Type);
        Assert.Equal(ZType.Int, right.Type);
    }

    [Fact]
    public void CallWithVarCalleeKeepsCalleeAndAnfsArgs()
    {
        var fn = V("f");
        var call = new IrNode.Call(fn, [AwaitTask("t"), Int(2)]) { Type = ZType.Int };

        var result = new AwaitHoister().Hoist(call);

        var outer = Assert.IsType<IrNode.Let>(result);
        var inner = Assert.IsType<IrNode.Let>(outer.Body);
        var rebuilt = Assert.IsType<IrNode.Call>(inner.Body);
        // The Var callee is never hoisted — EmitCall relies on direct-name resolution.
        Assert.Same(fn, rebuilt.Function);
        Assert.Equal(2, rebuilt.Args.Count);
        Assert.All(rebuilt.Args, a => Assert.IsType<IrNode.Var>(a));
    }

    [Fact]
    public void CallWithNonVarCalleeHoistsCalleeToo()
    {
        var call = new IrNode.Call(AwaitTask("tf"), [Int(1)]) { Type = ZType.Int };

        var result = new AwaitHoister().Hoist(call);

        var outer = Assert.IsType<IrNode.Let>(result);
        Assert.Equal("__await_hoist_0", outer.VarName);
        Assert.IsType<IrNode.Await>(outer.Value);

        var inner = Assert.IsType<IrNode.Let>(outer.Body);
        var rebuilt = Assert.IsType<IrNode.Call>(inner.Body);
        var callee = Assert.IsType<IrNode.Var>(rebuilt.Function);
        Assert.Equal("__await_hoist_0", callee.Name);
        Assert.Single(rebuilt.Args);
    }

    [Fact]
    public void AwaitNodeItselfIsNeverLetWrapped()
    {
        var result = new AwaitHoister().Hoist(AwaitTask("t"));

        // The Await sits at Let.Value positions when hoisted from operands, but a
        // top-level Await is rewritten in place, not ANF'd around itself.
        Assert.IsType<IrNode.Await>(result);
    }

    [Fact]
    public void TypeAndTailCallArePreservedOnRebuiltNodes()
    {
        var binop = new IrNode.BinOp("+", Int(1), AwaitTask("t"))
        {
            Type = ZType.Int,
            IsTailCall = true,
        };

        var result = new AwaitHoister().Hoist(binop);

        var outer = Assert.IsType<IrNode.Let>(result);
        var inner = Assert.IsType<IrNode.Let>(outer.Body);
        var rebuilt = Assert.IsType<IrNode.BinOp>(inner.Body);
        Assert.Equal(ZType.Int, rebuilt.Type);
        Assert.True(rebuilt.IsTailCall);
        // The Let spine takes the type of the expression it computes.
        Assert.Equal(ZType.Int, outer.Type);
    }

    [Fact]
    public void OriginalSpanIsRestoredOnRewrittenRoot()
    {
        var span = new SourceSpan("test.zs", 3, 7, 11);
        var binop = new IrNode.BinOp("+", Int(1), AwaitTask("t"))
        {
            Type = ZType.Int,
            Span = span,
        };

        var result = new AwaitHoister().Hoist(binop);

        Assert.Equal(span, result.Span);
    }

    [Fact]
    public void RecursesIntoFuncDefBody()
    {
        var body = new IrNode.BinOp("+", Int(1), AwaitTask("t")) { Type = ZType.Int };
        var fn = new IrNode.FuncDef("f", [], ZType.Int, body, IsSelfRecursive: false)
        {
            Type = ZType.Unit,
            IsAsync = true,
        };

        var result = new AwaitHoister().Hoist(fn);

        var rebuilt = Assert.IsType<IrNode.FuncDef>(result);
        Assert.IsType<IrNode.Let>(rebuilt.Body);
    }

    [Fact]
    public void RecursesIntoClassDeclMethodsAndConstructor()
    {
        var awaitingBinOp = new IrNode.BinOp("+", Int(1), AwaitTask("t")) { Type = ZType.Int };
        var method = new IrObjectMethod("m", [], ZType.Int, awaitingBinOp, IsAsync: true);
        var ctor = new IrConstructor(
            [],
            SuperArgs: null,
            FieldSets: [("f", awaitingBinOp)],
            BodyExprs: [awaitingBinOp]
        );
        var cd = new IrNode.ClassDecl("C", [], [], [], [method], Constructor: ctor)
        {
            Type = ZType.Unit,
        };

        var result = new AwaitHoister().Hoist(cd);

        var rebuilt = Assert.IsType<IrNode.ClassDecl>(result);
        Assert.IsType<IrNode.Let>(Assert.Single(rebuilt.Methods).Body);
        Assert.NotNull(rebuilt.Constructor);
        Assert.IsType<IrNode.Let>(Assert.Single(rebuilt.Constructor.FieldSets).Value);
        Assert.IsType<IrNode.Let>(Assert.Single(rebuilt.Constructor.BodyExprs));
    }
}
