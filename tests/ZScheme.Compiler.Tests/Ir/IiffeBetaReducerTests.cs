using Xunit;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class IiffeBetaReducerTests
{
    private static IrNode.FuncDef Lambda(
        IReadOnlyList<IrParam> parms,
        IrNode body,
        bool isSelfRecursive = false,
        bool isAsync = false,
        IReadOnlyList<string>? typeParams = null,
        string? clrDelegateTypeName = null
    ) =>
        new(
            "__lambda_1_1",
            parms,
            body.Type,
            body,
            isSelfRecursive,
            typeParams,
            IsAsync: isAsync,
            ClrDelegateTypeName: clrDelegateTypeName
        )
        {
            Type = ZType.Int,
        };

    private static IrNode.Var V(string name) => new(name) { Type = ZType.Int };

    [Fact]
    public void ImmediatelyInvokedLambda_BecomesLetSpine()
    {
        // ((lambda (x y) (+ x y)) 1 2)  =>  (let x 1 (let y 2 (+ x y)))
        var lambda = Lambda(
            [new IrParam("x", ZType.Int), new IrParam("y", ZType.Int)],
            new IrNode.BinOp("+", V("x"), V("y")) { Type = ZType.Int }
        );
        var call = new IrNode.Call(lambda, [new IrNode.IntConst(1), new IrNode.IntConst(2)])
        {
            Type = ZType.Int,
        };

        var result = new IiffeBetaReducer().Reduce(call);

        var outer = Assert.IsType<IrNode.Let>(result);
        Assert.Equal("x", outer.VarName);
        Assert.Equal(ZType.Int, outer.VarType);
        Assert.IsType<IrNode.IntConst>(outer.Value);

        var inner = Assert.IsType<IrNode.Let>(outer.Body);
        Assert.Equal("y", inner.VarName);
        Assert.IsType<IrNode.BinOp>(inner.Body);
    }

    [Fact]
    public void ZeroArgImmediatelyInvokedLambda_BecomesBodyDirectly()
    {
        // ((lambda () 42))  =>  42  (no params, so the spine is just the body)
        var lambda = Lambda([], new IrNode.IntConst(42) { Type = ZType.Int });
        var call = new IrNode.Call(lambda, []) { Type = ZType.Int };

        var result = new IiffeBetaReducer().Reduce(call);

        Assert.IsType<IrNode.IntConst>(result);
    }

    [Fact]
    public void LambdaUsedAsValue_IsNotReduced()
    {
        // (f (lambda (x) x)) — the lambda is an argument, not the call target, so it
        // must remain a FuncDef (a first-class value), never beta-reduced.
        var lambda = Lambda([new IrParam("x", ZType.Int)], V("x"));
        var call = new IrNode.Call(V("f"), [lambda]) { Type = ZType.Int };

        var result = new IiffeBetaReducer().Reduce(call);

        var resultCall = Assert.IsType<IrNode.Call>(result);
        Assert.IsType<IrNode.FuncDef>(resultCall.Args[0]);
    }

    [Fact]
    public void SelfRecursiveLambda_IsNotReduced()
    {
        var lambda = Lambda([new IrParam("x", ZType.Int)], V("x"), isSelfRecursive: true);
        var call = new IrNode.Call(lambda, [new IrNode.IntConst(1)]) { Type = ZType.Int };

        var result = new IiffeBetaReducer().Reduce(call);

        var resultCall = Assert.IsType<IrNode.Call>(result);
        Assert.IsType<IrNode.FuncDef>(resultCall.Function);
    }

    [Fact]
    public void AsyncLambda_IsNotReduced()
    {
        var lambda = Lambda([new IrParam("x", ZType.Int)], V("x"), isAsync: true);
        var call = new IrNode.Call(lambda, [new IrNode.IntConst(1)]) { Type = ZType.Int };

        var result = new IiffeBetaReducer().Reduce(call);

        Assert.IsType<IrNode.FuncDef>(Assert.IsType<IrNode.Call>(result).Function);
    }

    [Fact]
    public void GenericLambda_IsNotReduced()
    {
        var lambda = Lambda([new IrParam("x", ZType.Int)], V("x"), typeParams: ["T"]);
        var call = new IrNode.Call(lambda, [new IrNode.IntConst(1)]) { Type = ZType.Int };

        var result = new IiffeBetaReducer().Reduce(call);

        Assert.IsType<IrNode.FuncDef>(Assert.IsType<IrNode.Call>(result).Function);
    }

    [Fact]
    public void ClrDelegateTypedLambda_IsNotReduced()
    {
        var lambda = Lambda(
            [new IrParam("x", ZType.Int)],
            V("x"),
            clrDelegateTypeName: "System.Func<int, int>"
        );
        var call = new IrNode.Call(lambda, [new IrNode.IntConst(1)]) { Type = ZType.Int };

        var result = new IiffeBetaReducer().Reduce(call);

        Assert.IsType<IrNode.FuncDef>(Assert.IsType<IrNode.Call>(result).Function);
    }

    [Fact]
    public void ArityMismatch_IsNotReduced()
    {
        // Two params, one argument — partial application; must not be reduced.
        var lambda = Lambda(
            [new IrParam("x", ZType.Int), new IrParam("y", ZType.Int)],
            new IrNode.BinOp("+", V("x"), V("y")) { Type = ZType.Int }
        );
        var call = new IrNode.Call(lambda, [new IrNode.IntConst(1)]) { Type = ZType.Int };

        var result = new IiffeBetaReducer().Reduce(call);

        Assert.IsType<IrNode.FuncDef>(Assert.IsType<IrNode.Call>(result).Function);
    }

    [Fact]
    public void VariadicLambda_IsNotReduced()
    {
        var lambda = Lambda([new IrParam("xs", ZType.Int, IsVariadic: true)], V("xs"));
        var call = new IrNode.Call(lambda, [new IrNode.IntConst(1)]) { Type = ZType.Int };

        var result = new IiffeBetaReducer().Reduce(call);

        Assert.IsType<IrNode.FuncDef>(Assert.IsType<IrNode.Call>(result).Function);
    }

    [Fact]
    public void ArgReferencingParamName_IsNotReduced()
    {
        // ((lambda (x) x) x) — the argument refers to a name equal to the param, which
        // the let spine would wrongly capture; reduction must be blocked.
        var lambda = Lambda([new IrParam("x", ZType.Int)], V("x"));
        var call = new IrNode.Call(lambda, [V("x")]) { Type = ZType.Int };

        var result = new IiffeBetaReducer().Reduce(call);

        Assert.IsType<IrNode.FuncDef>(Assert.IsType<IrNode.Call>(result).Function);
    }

    [Fact]
    public void NestedImmediatelyInvokedLambda_IsAlsoReduced()
    {
        // outer arg is itself an IIFE: ((lambda (x) x) ((lambda (y) y) 1))
        var inner = new IrNode.Call(
            Lambda([new IrParam("y", ZType.Int)], V("y")),
            [new IrNode.IntConst(1)]
        )
        {
            Type = ZType.Int,
        };
        var outer = new IrNode.Call(Lambda([new IrParam("x", ZType.Int)], V("x")), [inner])
        {
            Type = ZType.Int,
        };

        var result = new IiffeBetaReducer().Reduce(outer);

        // Outer reduces to (let x <inner-reduced> x); inner reduces to (let y 1 y).
        var outerLet = Assert.IsType<IrNode.Let>(result);
        Assert.Equal("x", outerLet.VarName);
        var innerLet = Assert.IsType<IrNode.Let>(outerLet.Value);
        Assert.Equal("y", innerLet.VarName);
    }
}
