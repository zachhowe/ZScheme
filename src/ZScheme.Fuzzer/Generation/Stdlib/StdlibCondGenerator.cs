namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates `(cond [test v] ... [else v])` expressions over Int. The cond
// macro lowers to nested `if` chains, so this exercises the
// macro-expansion-then-IR-lowering path that the literal `if` form skips.
//
// Two- and three-arm shapes are produced; an explicit `[else ...]` fallback
// is always present so the resulting expression is total at type-check time.
public sealed class StdlibCondGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibCondGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported() => _ctx.Imports.Contains(StdlibImport.Cond);

    public string CondToInt(Scope scope, int depth)
    {
        var numTests = 1 + _ctx.Rng.Next(3); // 1..3 test arms, plus mandatory else
        var arms = new List<string>(numTests + 1);
        for (var i = 0; i < numTests; i++)
        {
            var test = _exprs.GenBool(scope, depth - 1);
            var value = _exprs.GenInt(scope, depth - 1);
            arms.Add($"[{test} {value}]");
        }
        var elseValue = _exprs.GenInt(scope, depth - 1);
        arms.Add($"[else {elseValue}]");
        return $"(cond {string.Join(" ", arms)})";
    }
}
