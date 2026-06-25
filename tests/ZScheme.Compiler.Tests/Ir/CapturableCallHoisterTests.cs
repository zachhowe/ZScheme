using Xunit;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ir;

/// <summary>
///     Direct unit tests for the IR-shape transformations performed by
///     <see cref="CapturableCallHoister"/>. Asserts on the structure produced for each
///     value-consuming position so the contract is locked in independent of the
///     end-to-end compiler pipeline.
/// </summary>
public class CapturableCallHoisterTests
{
    private static IrNode.ClrCall CallCc(ZType resultType) =>
        new(
            "ZScheme.Runtime.Runtime",
            "CallCcTyped",
            [new IrNode.Var("user-fn") { Type = resultType }],
            GenericArity: 2,
            GenericTypeArgs: [resultType, resultType]
        )
        {
            Type = resultType,
        };

    private static IrNode.ClrCall ShiftCall(ZType resultType) =>
        new(
            "ZScheme.Runtime.Runtime",
            "ShiftTyped",
            [new IrNode.Var("body-fn") { Type = resultType }],
            GenericArity: 2,
            GenericTypeArgs: [resultType, resultType]
        )
        {
            Type = resultType,
        };

    [Fact]
    public void Hoist_NoCapturable_LeavesIrUnchanged()
    {
        // Pure binop: no capturable call → no rewriting.
        var input = new IrNode.BinOp(
            "+",
            new IrNode.IntConst(1) { Type = ZType.Int },
            new IrNode.IntConst(2) { Type = ZType.Int }
        )
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        // No new Lets introduced.
        Assert.IsType<IrNode.BinOp>(output);
    }

    [Fact]
    public void Hoist_BinOpRight_LiftsCallToOuterLet()
    {
        // (+ 100 (call/cc f)) → (let __cc_hoist_0 (call/cc f) (+ 100 __cc_hoist_0))
        var input = new IrNode.BinOp(
            "+",
            new IrNode.IntConst(100) { Type = ZType.Int },
            CallCc(ZType.Int)
        )
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        // The top-level node is now a Let whose value is the call/cc.
        var let = Assert.IsType<IrNode.Let>(output);
        Assert.IsType<IrNode.ClrCall>(let.Value);
        // The let-body is the BinOp with the call replaced by a Var reference.
        var binop = Assert.IsType<IrNode.BinOp>(let.Body);
        Assert.IsType<IrNode.IntConst>(binop.Left);
        Assert.IsType<IrNode.Var>(binop.Right);
    }

    [Fact]
    public void Hoist_BinOpLeft_LiftsCallToOuterLet()
    {
        // ((call/cc f) + 100) → (let __cc_hoist_0 (call/cc f) (+ __cc_hoist_0 100))
        var input = new IrNode.BinOp(
            "+",
            CallCc(ZType.Int),
            new IrNode.IntConst(100) { Type = ZType.Int }
        )
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        var let = Assert.IsType<IrNode.Let>(output);
        Assert.IsType<IrNode.ClrCall>(let.Value);
        var binop = Assert.IsType<IrNode.BinOp>(let.Body);
        Assert.IsType<IrNode.Var>(binop.Left);
        Assert.IsType<IrNode.IntConst>(binop.Right);
    }

    [Fact]
    public void Hoist_AndOperator_DoesNotLift_ToPreserveShortCircuit()
    {
        // (and (call/cc f) #f) — short-circuit semantics forbid hoisting either operand.
        // The hoister leaves and/or untouched, accepting that capture through them remains
        // a known minor limitation (matches WithHandlersHoister's stance).
        var input = new IrNode.BinOp(
            "and",
            CallCc(ZType.Bool),
            new IrNode.BoolConst(false) { Type = ZType.Bool }
        )
        {
            Type = ZType.Bool,
        };

        var output = new CapturableCallHoister().Hoist(input);

        // Top-level still a BinOp, no enclosing Let.
        var binop = Assert.IsType<IrNode.BinOp>(output);
        Assert.Equal("and", binop.Op);
    }

    [Fact]
    public void Hoist_OrOperator_DoesNotLift_ToPreserveShortCircuit()
    {
        var input = new IrNode.BinOp(
            "or",
            CallCc(ZType.Bool),
            new IrNode.BoolConst(false) { Type = ZType.Bool }
        )
        {
            Type = ZType.Bool,
        };

        var output = new CapturableCallHoister().Hoist(input);

        var binop = Assert.IsType<IrNode.BinOp>(output);
        Assert.Equal("or", binop.Op);
    }

    [Fact]
    public void Hoist_FunctionCallArg_LiftsCall()
    {
        // (some-fn (call/cc f)) → (let __cc_hoist_0 (call/cc f) (some-fn __cc_hoist_0))
        var input = new IrNode.Call(
            new IrNode.Var("some-fn") { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) },
            [CallCc(ZType.Int)]
        )
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        var let = Assert.IsType<IrNode.Let>(output);
        Assert.IsType<IrNode.ClrCall>(let.Value);
        var call = Assert.IsType<IrNode.Call>(let.Body);
        Assert.IsType<IrNode.Var>(call.Args[0]);
    }

    [Fact]
    public void Hoist_IfCondition_LiftsConditionToLetAroundIf()
    {
        // (if (call/cc f) 1 2) — condition position. The if would have type Int but the
        // call/cc result type is also Int (and used as the condition). The Let must wrap
        // the entire if so the call/cc-bearing Let-value is at the if's parent level.
        var input = new IrNode.If(
            CallCc(ZType.Bool),
            new IrNode.IntConst(1) { Type = ZType.Int },
            new IrNode.IntConst(2) { Type = ZType.Int }
        )
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        var let = Assert.IsType<IrNode.Let>(output);
        Assert.IsType<IrNode.ClrCall>(let.Value);
        Assert.IsType<IrNode.If>(let.Body);
    }

    [Fact]
    public void Hoist_IfBranches_LeftIntact_NotPulledOut()
    {
        // (if cond (call/cc f) 99) — branches are conditionally evaluated; must NOT be
        // hoisted into a Let around the if (would change semantics).
        var input = new IrNode.If(
            new IrNode.BoolConst(true) { Type = ZType.Bool },
            CallCc(ZType.Int),
            new IrNode.IntConst(99) { Type = ZType.Int }
        )
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        // Top-level remains an If; the call/cc stays inside the then-branch.
        var ifNode = Assert.IsType<IrNode.If>(output);
        Assert.IsType<IrNode.ClrCall>(ifNode.Then);
    }

    [Fact]
    public void Hoist_MatchScrutinee_LiftsScrutineeToLet()
    {
        var input = new IrNode.Match(
            CallCc(ZType.Int),
            [new IrMatchArm(new IrPattern.Wildcard(), new IrNode.IntConst(99) { Type = ZType.Int })]
        )
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        var let = Assert.IsType<IrNode.Let>(output);
        Assert.IsType<IrNode.ClrCall>(let.Value);
        Assert.IsType<IrNode.Match>(let.Body);
    }

    [Fact]
    public void Hoist_TupleNew_LiftsElementsContainingCalls()
    {
        // (tuple 1 (call/cc f) 3) → (let v (call/cc f) (tuple 1 v 3))
        var input = new IrNode.TupleNew([
            new IrNode.IntConst(1) { Type = ZType.Int },
            CallCc(ZType.Int),
            new IrNode.IntConst(3) { Type = ZType.Int },
        ])
        {
            Type = new ZType.ZNamedType("Tuple", [ZType.Int, ZType.Int, ZType.Int]),
        };

        var output = new CapturableCallHoister().Hoist(input);

        var let = Assert.IsType<IrNode.Let>(output);
        Assert.IsType<IrNode.ClrCall>(let.Value);
        var tn = Assert.IsType<IrNode.TupleNew>(let.Body);
        Assert.IsType<IrNode.Var>(tn.Elements[1]);
    }

    [Fact]
    public void Hoist_RecursesIntoLetValueAndBody()
    {
        // (let v 1 (+ v (call/cc f))) → (let v 1 (let __cc_hoist_0 (call/cc f) (+ v __cc_hoist_0)))
        var input = new IrNode.Let(
            "v",
            new IrNode.IntConst(1) { Type = ZType.Int },
            new IrNode.BinOp("+", new IrNode.Var("v") { Type = ZType.Int }, CallCc(ZType.Int))
            {
                Type = ZType.Int,
            }
        )
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        // Outer let is preserved; inner expression now has a hoisted let around the call/cc.
        var outerLet = Assert.IsType<IrNode.Let>(output);
        Assert.Equal("v", outerLet.VarName);
        var innerLet = Assert.IsType<IrNode.Let>(outerLet.Body);
        Assert.IsType<IrNode.ClrCall>(innerLet.Value);
    }

    [Fact]
    public void Hoist_DoesNotDoubleHoist_DirectLetValue()
    {
        // (let v (call/cc f) body) — call/cc is already in let-value position; no hoist
        // should be introduced around it.
        var input = new IrNode.Let("v", CallCc(ZType.Int), new IrNode.Var("v") { Type = ZType.Int })
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        // No new outer let — the original Let is returned essentially unchanged.
        var let = Assert.IsType<IrNode.Let>(output);
        Assert.Equal("v", let.VarName);
        Assert.IsType<IrNode.ClrCall>(let.Value);
    }

    [Fact]
    public void Hoist_RecursesIntoFuncDefBody()
    {
        // FuncDef wraps the body of synthesized reset/shift thunks; the hoister must
        // descend into FuncDef bodies so capturable calls inside reset/prompt thunks are
        // also lifted.
        var bodyExpr = new IrNode.BinOp(
            "+",
            new IrNode.IntConst(1) { Type = ZType.Int },
            ShiftCall(ZType.Int)
        )
        {
            Type = ZType.Int,
        };

        var input = new IrNode.FuncDef("thunk", [], ZType.Int, bodyExpr, IsSelfRecursive: false)
        {
            Type = new ZType.ZFuncType([], ZType.Int),
        };

        var output = new CapturableCallHoister().Hoist(input);

        var fn = Assert.IsType<IrNode.FuncDef>(output);
        // The body is now a Let with the shift call hoisted out.
        var let = Assert.IsType<IrNode.Let>(fn.Body);
        Assert.IsType<IrNode.ClrCall>(let.Value);
    }

    [Fact]
    public void Hoist_AllCaptureForms_AreRecognized()
    {
        // The hoister must lift CallCcTyped, ShiftTyped, ControlTyped, CallCompTyped, Reset
        // and their tagged variants. Verify by smoke-testing each via a BinOp arg.
        var captureMethods = new[]
        {
            "CallCcTyped",
            "ShiftTyped",
            "ShiftTypedAt",
            "ControlTyped",
            "ControlTypedAt",
            "CallCompTyped",
            "CallCompTypedAt",
            "Reset",
            "ResetAt",
        };

        foreach (var method in captureMethods)
        {
            var capturableCall = new IrNode.ClrCall(
                "ZScheme.Runtime.Runtime",
                method,
                [new IrNode.Var("user-fn") { Type = ZType.Int }],
                GenericArity: 1,
                GenericTypeArgs: [ZType.Int]
            )
            {
                Type = ZType.Int,
            };
            var input = new IrNode.BinOp(
                "+",
                new IrNode.IntConst(1) { Type = ZType.Int },
                capturableCall
            )
            {
                Type = ZType.Int,
            };

            var output = new CapturableCallHoister().Hoist(input);

            Assert.IsType<IrNode.Let>(output);
        }
    }

    [Fact]
    public void Hoist_NonTrivialEarlierOperand_BoundToPreserveOrder()
    {
        // ((side-effect-fn) (call/cc f)) — the side-effect call is non-capturable but
        // non-trivial, so it must be bound to a Let that runs BEFORE the capturable one.
        // Otherwise hoisting flips evaluation order.
        var sideEffectCall = new IrNode.Call(
            new IrNode.Var("side-effect") { Type = new ZType.ZFuncType([], ZType.Int) },
            []
        )
        {
            Type = ZType.Int,
        };
        var input = new IrNode.BinOp("+", sideEffectCall, CallCc(ZType.Int)) { Type = ZType.Int };

        var output = new CapturableCallHoister().Hoist(input);

        // Outermost let binds side-effect (the earlier operand); its body is a let binding
        // the call/cc; that let's body is the BinOp.
        var outerLet = Assert.IsType<IrNode.Let>(output);
        Assert.IsType<IrNode.Call>(outerLet.Value);
        var innerLet = Assert.IsType<IrNode.Let>(outerLet.Body);
        Assert.IsType<IrNode.ClrCall>(innerLet.Value);
        Assert.IsType<IrNode.BinOp>(innerLet.Body);
    }

    [Fact]
    public void Hoist_TrivialEarlierOperand_StaysInline()
    {
        // (1 + (call/cc f)) — earlier operand is a literal, no side effects, can stay
        // inline. The hoister produces a single Let around the BinOp.
        var input = new IrNode.BinOp(
            "+",
            new IrNode.IntConst(1) { Type = ZType.Int },
            CallCc(ZType.Int)
        )
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        var let = Assert.IsType<IrNode.Let>(output);
        Assert.IsType<IrNode.ClrCall>(let.Value);
        var binop = Assert.IsType<IrNode.BinOp>(let.Body);
        // Left operand is the original literal, not a Var — confirming we kept it inline.
        Assert.IsType<IrNode.IntConst>(binop.Left);
    }

    [Fact]
    public void Hoist_NonCapturableClrCall_NotLifted()
    {
        // Only the specific runtime capture entry-points are detected. A regular CLR call
        // (e.g. SomeOther.Method) should NOT trigger hoisting even when nested in a BinOp.
        var nonCapturable = new IrNode.ClrCall(
            "System.Math",
            "Abs",
            [new IrNode.Var("x") { Type = ZType.Int }]
        )
        {
            Type = ZType.Int,
        };
        var input = new IrNode.BinOp(
            "+",
            new IrNode.IntConst(1) { Type = ZType.Int },
            nonCapturable
        )
        {
            Type = ZType.Int,
        };

        var output = new CapturableCallHoister().Hoist(input);

        // No hoist let introduced.
        Assert.IsType<IrNode.BinOp>(output);
    }
}
