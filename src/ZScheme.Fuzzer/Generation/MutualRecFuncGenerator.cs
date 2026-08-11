namespace ZScheme.Fuzzer.Generation;

// Generates a pair of mutually-recursive Int->Int functions. Both functions
// hard-decrement their first argument and bottom out on `n <= 0`, so they
// always terminate regardless of how callers compose them.
//
// Mutual recursion is not produced by UserFuncGenerator (which only emits
// single-function recursion). It exercises the compiler's module-level
// define ordering — in particular, AstBuilder/TypeInferer's tolerance of
// forward references between sibling top-level `define`s and the resulting
// IL backend hoisting (see Ir/AwaitHoister, ClosureConverter for the related
// passes).
//
// Each function takes `[n : Int]` and returns Int. Body shape:
//   (if (<= n 0) <base-int> (<other-fn> (- n 1)))
//   (if (<= n 0) <base-int> (+ <leaf> (<other-fn> (- n 1))))   ;; non-tail variant
// Picking between tail / non-tail forms exercises both TCO-eligible and
// non-TCO call shapes for the mutually-recursive case.
public sealed class MutualRecFuncGenerator
{
    private static readonly IReadOnlySet<ExprType> OnlyInt = new HashSet<ExprType> { ExprType.Int };

    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public MutualRecFuncGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Returns the two function definitions in order. Both are appended by the
    // caller to the program text and added to _ctx.UserFuncs so subsequent
    // generators (e.g. ExprGenerator's GenCall) can call them.
    public (UserFunc First, UserFunc Second) GeneratePair(string nameA, string nameB)
    {
        var defA = BuildOne(nameA, nameB);
        var defB = BuildOne(nameB, nameA);
        return (defA, defB);
    }

    private UserFunc BuildOne(string self, string other)
    {
        var nParam = _ctx.Fresh();
        var scope = new Scope().Extend(nParam, ExprType.Int);
        // Bound body depth so each function's base/leaf expressions stay small —
        // mutual recursion already adds inter-function call overhead at runtime
        // and the differential oracle bounds total program runtime.
        var bodyDepth = Math.Min(_ctx.MaxDepth, 3);
        var baseExpr = _exprs.GenInt(scope, bodyDepth);

        // Forced tail under --deep-recursion, for the reason UserFuncGenerator spells out.
        // Note a mutual tail call is not a *self* call, so TailCallLowering leaves it as a real
        // call — meaning this pair overflows at depth on both backends, which the oracle reads
        // as agreement.
        var isTail = _ctx.DeepRecursion || _ctx.Rng.NextDouble() < 0.75;
        var recCall = $"({other} (- {nParam} 1))";
        var step = isTail ? recCall : $"(+ {_exprs.GenInt(scope, bodyDepth)} {recCall})";
        var body = $"(if (<= {nParam} 0) {baseExpr} {step})";

        var def = $"(define ({self} [{nParam} : Int]) : Int\n  {body})";
        // Marked as Recursive so existing GenCall handles the first-arg-bounded
        // small-Int constraint when it picks this function — same termination
        // contract as single-function recursion.
        return new UserFunc(
            self,
            UserFuncKind.Recursive,
            [ExprType.Int],
            def,
            OnlyInt,
            [false],
            false
        );
    }
}
