namespace ZScheme.Fuzzer.Generation;

// Emits `((partial f a1 ... aN-1) aN)` for a user function `f` of arity N >= 2.
// Exercises the `LowerPartial` path (lambda-wrapper emission + type-argument
// resolution from applied args).
//
// Recursive user funcs are excluded: their first parameter is the recursion
// counter, bounded to [0,20] only at direct `GenCall` sites. Partially applying
// a recursive function would bypass that bound, risking stack-overflow divergence
// between the IL backend (TCO-tagged) and the C# backend (not always TCO-reducible
// via Roslyn).
public sealed class PartialExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public PartialExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public static bool HasEligible(GeneratorContext ctx) =>
        ctx.UserFuncs.Any(IsEligible);

    private static bool IsEligible(UserFunc f) =>
        f.ParamTypes.Count >= 2 && f.Kind != UserFuncKind.Recursive;

    public string PartialApplyToInt(Scope scope, int depth)
    {
        var eligible = _ctx.UserFuncs.Where(IsEligible).ToList();
        var f = eligible[_ctx.Rng.Next(eligible.Count)];

        var prefixArgs = new List<string>();
        for (var i = 0; i < f.ParamTypes.Count - 1; i++)
            prefixArgs.Add(GenArg(f.ParamTypes[i], scope, depth - 1));

        var finalArg = GenArg(f.ParamTypes[^1], scope, depth - 1);

        return $"((partial {f.Name} {string.Join(" ", prefixArgs)}) {finalArg})";
    }

    private string GenArg(ExprType t, Scope scope, int depth) =>
        t switch
        {
            ExprType.Int => _exprs.GenInt(scope, depth),
            ExprType.IntFn => _exprs.GenIntFnArg(scope, depth),
            _ => throw new InvalidOperationException($"Unsupported partial arg type: {t}"),
        };
}
