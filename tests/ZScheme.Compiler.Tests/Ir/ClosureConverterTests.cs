using Xunit;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

public class ClosureConverterTests
{
    // A variable reference, typed Int by default.
    private static IrNode.Var V(string name, ZType? type = null)
    {
        return new IrNode.Var(name) { Type = type ?? ZType.Int };
    }

    // A lambda (FuncDef), typed by its own signature so it looks non-generic to the pass.
    private static IrNode.FuncDef Lambda(IrParam[] parms, IrNode body, ZType? type = null)
    {
        return new IrNode.FuncDef("lambda", parms, ZType.Int, body, false)
        {
            Type = type ?? new ZType.ZFuncType([.. parms.Select(p => p.Type)], ZType.Int),
        };
    }

    // Wraps `lambda` as the body of an outer function whose params bind `outerBindings`, so those
    // names are enclosing locals the lambda may capture. Returns the converted outer body (the
    // lambda after conversion — a Closure if it was lifted) and the converter.
    private static (IrNode body, ClosureConverter conv) ConvertNested(
        IrNode.FuncDef lambda,
        params (string Name, ZType Type)[] outerBindings
    )
    {
        var outer = new IrNode.FuncDef(
            "outer",
            outerBindings.Select(b => new IrParam(b.Name, b.Type)).ToList(),
            ZType.Int,
            lambda,
            false
        )
        {
            Type = ZType.Int,
        };
        var conv = new ClosureConverter();
        var result = Assert.IsType<IrNode.FuncDef>(conv.Convert(outer));
        return (result.Body, conv);
    }

    [Fact]
    public void TopLevelFunction_ReferencingOnlyParamsAndGlobals_NotLifted()
    {
        // (define (add x y) (+ x y)) — plus a call to a global `g`. Nothing is bound in an
        // enclosing local scope, so there is nothing to capture and no lifting.
        var func = new IrNode.FuncDef(
            "add",
            [new IrParam("x", ZType.Int), new IrParam("y", ZType.Int)],
            ZType.Int,
            new IrNode.Call(V("g"), [new IrNode.BinOp("+", V("x"), V("y")) { Type = ZType.Int }])
            {
                Type = ZType.Int,
            },
            false
        )
        {
            Type = ZType.Int,
        };

        var conv = new ClosureConverter();
        var result = conv.Convert(func);

        Assert.IsType<IrNode.FuncDef>(result);
        Assert.Empty(conv.LiftedFunctions);
    }

    [Fact]
    public void CapturingLambda_ReplacedWithClosure()
    {
        // outer(a) { (lambda (x) (+ x a)) } — the lambda captures a (outer's param).
        var lambda = Lambda(
            [new IrParam("x", ZType.Int)],
            new IrNode.BinOp("+", V("x"), V("a")) { Type = ZType.Int }
        );
        var (body, conv) = ConvertNested(lambda, ("a", ZType.Int));

        var closure = Assert.IsType<IrNode.Closure>(body);
        Assert.Equal(
            ["a"],
            closure.CapturedValues.OfType<IrNode.Var>().Select(v => v.Name)
        );
        Assert.Single(conv.LiftedFunctions);
    }

    [Fact]
    public void Captures_CarryRealInferredTypes()
    {
        // Capture a String-typed variable; both the capture param and the captured value Var
        // must carry the real type, not Unit.
        var lambda = Lambda(
            [new IrParam("x", ZType.Int)],
            new IrNode.BinOp("+", V("x"), V("a", ZType.String)) { Type = ZType.String }
        );
        var (body, conv) = ConvertNested(lambda, ("a", ZType.String));

        var closure = Assert.IsType<IrNode.Closure>(body);
        var capturedVar = Assert.IsType<IrNode.Var>(Assert.Single(closure.CapturedValues));
        Assert.Equal(ZType.String, capturedVar.Type);

        var lifted = Assert.Single(conv.LiftedFunctions);
        Assert.Equal("a", lifted.Params[0].Name);
        Assert.Equal(ZType.String, lifted.Params[0].Type);
    }

    [Fact]
    public void LiftedFunction_HasCaptureParamsPrependedBeforeOriginalParams()
    {
        var lambda = Lambda(
            [new IrParam("x", ZType.Int)],
            new IrNode.BinOp("+", V("x"), V("a")) { Type = ZType.Int }
        );
        var (_, conv) = ConvertNested(lambda, ("a", ZType.Int));

        var lifted = Assert.Single(conv.LiftedFunctions);
        Assert.Equal(2, lifted.Params.Count);
        Assert.Equal("a", lifted.Params[0].Name); // capture first
        Assert.Equal("x", lifted.Params[1].Name); // then original
        Assert.Null(lifted.ClrDelegateTypeName); // lifted static keeps no delegate type
    }

    [Fact]
    public void GlobalReference_NotCaptured()
    {
        // outer(a) { (lambda (x) (g x a)) } — g is a global (not an enclosing local), so only a
        // is captured.
        var lambda = Lambda(
            [new IrParam("x", ZType.Int)],
            new IrNode.Call(V("g"), [V("x"), V("a")]) { Type = ZType.Int }
        );
        var (body, conv) = ConvertNested(lambda, ("a", ZType.Int));

        var closure = Assert.IsType<IrNode.Closure>(body);
        var captured = closure.CapturedValues.OfType<IrNode.Var>().Select(v => v.Name).ToHashSet();
        Assert.Contains("a", captured);
        Assert.DoesNotContain("g", captured);
        Assert.DoesNotContain("x", captured);
    }

    [Fact]
    public void LetBoundVarInLambda_NotCaptured()
    {
        // outer(a) { (lambda () (let ([y 5]) (+ y a))) } — y is bound by the lambda's own let,
        // only a is captured.
        var lambda = Lambda(
            [],
            new IrNode.Let(
                "y",
                new IrNode.IntConst(5) { Type = ZType.Int },
                new IrNode.BinOp("+", V("y"), V("a")) { Type = ZType.Int }
            )
            {
                Type = ZType.Int,
            }
        );
        var (body, conv) = ConvertNested(lambda, ("a", ZType.Int));

        var closure = Assert.IsType<IrNode.Closure>(body);
        var captured = closure.CapturedValues.OfType<IrNode.Var>().Select(v => v.Name).ToHashSet();
        Assert.Contains("a", captured);
        Assert.DoesNotContain("y", captured);
    }

    [Fact]
    public void CaptureLessLambda_LeftAsFuncDef()
    {
        // outer(a) { (lambda (x) (+ x 1)) } — the lambda captures nothing, so it stays a bare
        // FuncDef for the backends' own emission.
        var lambda = Lambda(
            [new IrParam("x", ZType.Int)],
            new IrNode.BinOp("+", V("x"), new IrNode.IntConst(1) { Type = ZType.Int })
            {
                Type = ZType.Int,
            }
        );
        var (body, conv) = ConvertNested(lambda, ("a", ZType.Int));

        Assert.IsType<IrNode.FuncDef>(body);
        Assert.Empty(conv.LiftedFunctions);
    }

    [Fact]
    public void NestedLambdas_BothLifted()
    {
        // outer(a) { (lambda (x) (lambda (y) (+ (+ x y) a))) } — inner captures x and a, outer
        // lambda captures a; both are lifted.
        var inner = Lambda(
            [new IrParam("y", ZType.Int)],
            new IrNode.BinOp(
                "+",
                new IrNode.BinOp("+", V("x"), V("y")) { Type = ZType.Int },
                V("a")
            )
            {
                Type = ZType.Int,
            }
        );
        var outerLambda = Lambda([new IrParam("x", ZType.Int)], inner);
        var (body, conv) = ConvertNested(outerLambda, ("a", ZType.Int));

        Assert.IsType<IrNode.Closure>(body);
        Assert.Equal(2, conv.LiftedFunctions.Count);
    }

    [Fact]
    public void FreeVar_InsideRecordNew_Captured()
    {
        var lambda = Lambda(
            [],
            new IrNode.RecordNew("R", [("f", V("a"))]) { Type = ZType.Int }
        );
        var (body, conv) = ConvertNested(lambda, ("a", ZType.Int));

        var closure = Assert.IsType<IrNode.Closure>(body);
        Assert.Contains("a", closure.CapturedValues.OfType<IrNode.Var>().Select(v => v.Name));
    }

    [Fact]
    public void FreeVar_InsideClrCall_Captured()
    {
        var lambda = Lambda(
            [],
            new IrNode.ClrCall("System.Console", "WriteLine", [V("a")]) { Type = ZType.Unit }
        );
        var (body, conv) = ConvertNested(lambda, ("a", ZType.Int));

        var closure = Assert.IsType<IrNode.Closure>(body);
        Assert.Contains("a", closure.CapturedValues.OfType<IrNode.Var>().Select(v => v.Name));
    }

    [Fact]
    public void WithHandlers_HandlerBindingNotCaptured_FreeVarCaptured()
    {
        // body: try { a } catch (Ex e) { (+ e a) } — e is handler-bound, a is free.
        var lambda = Lambda(
            [],
            new IrNode.WithHandlers(
                V("a"),
                [
                    new IrHandlerClause(
                        "System.Exception",
                        "e",
                        new IrNode.BinOp("+", V("e"), V("a")) { Type = ZType.Int }
                    ),
                ]
            )
            {
                Type = ZType.Int,
            }
        );
        var (body, conv) = ConvertNested(lambda, ("a", ZType.Int));

        var closure = Assert.IsType<IrNode.Closure>(body);
        var captured = closure.CapturedValues.OfType<IrNode.Var>().Select(v => v.Name).ToHashSet();
        Assert.Contains("a", captured);
        Assert.DoesNotContain("e", captured);
    }

    [Fact]
    public void LambdaCapturingOuterGenerics_NotLifted()
    {
        // A lambda whose type mentions a free type variable refers to an enclosing generic
        // function's type parameter; it must be left for the backends' own lambda path.
        var typeVar = new ZType.ZTypeVar(0);
        var lambda = Lambda(
            [new IrParam("x", typeVar)],
            new IrNode.BinOp("+", V("x", typeVar), V("a", typeVar)) { Type = typeVar },
            new ZType.ZFuncType([typeVar], typeVar)
        );
        var (body, conv) = ConvertNested(lambda, ("a", typeVar));

        Assert.IsType<IrNode.FuncDef>(body);
        Assert.Empty(conv.LiftedFunctions);
    }

    [Fact]
    public void LambdaInsideClassDecl_NotLifted()
    {
        // A capturing lambda inside a class method is left untouched — the whole ClassDecl
        // subtree is skipped, since this pass cannot see class fields / `this`.
        var lambda = Lambda(
            [new IrParam("x", ZType.Int)],
            new IrNode.BinOp("+", V("x"), V("p")) { Type = ZType.Int }
        );
        var method = new IrObjectMethod("m", [new IrParam("p", ZType.Int)], ZType.Int, lambda);
        var cls = new IrNode.ClassDecl("C", [], [], [], [method]) { Type = ZType.Unit };
        var seq = new IrNode.Seq([cls]) { Type = ZType.Unit };

        var conv = new ClosureConverter();
        var result = conv.Convert(seq);

        Assert.Empty(conv.LiftedFunctions);
        // The class subtree is returned unchanged (reference-equal).
        Assert.Same(cls, Assert.Single(Assert.IsType<IrNode.Seq>(result).Nodes));
    }

    [Fact]
    public void MultipleClosures_GetUniqueNames()
    {
        // outer(a, b) { (tuple (lambda () a) (lambda () b)) } — two sibling capturing lambdas
        // directly in the outer body (not nested in a third), so exactly two are lifted.
        var lam1 = Lambda([], V("a"));
        var lam2 = Lambda([], V("b"));
        var outer = new IrNode.FuncDef(
            "outer",
            [new IrParam("a", ZType.Int), new IrParam("b", ZType.Int)],
            ZType.Int,
            new IrNode.TupleNew([lam1, lam2]) { Type = ZType.Int },
            false
        )
        {
            Type = ZType.Int,
        };

        var conv = new ClosureConverter();
        conv.Convert(outer);

        Assert.Equal(2, conv.LiftedFunctions.Count);
        Assert.NotEqual(conv.LiftedFunctions[0].Name, conv.LiftedFunctions[1].Name);
    }
}
