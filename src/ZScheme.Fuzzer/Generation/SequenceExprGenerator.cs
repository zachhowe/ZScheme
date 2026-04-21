namespace ZScheme.Fuzzer.Generation;

// Emits `(begin e1 e2 ... final)` forms that reduce to Int.
// The final expression is always Int so the result flows back into GenInt callers.
// Intermediates are Int, Bool, or Float value-producing expressions — no `(raise ...)`,
// which would leave the sequencing tail as dead code and stress Roslyn's reachability
// rules. Exception-raising is owned by ExceptionExprGenerator.
public sealed class SequenceExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public SequenceExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public string BeginToInt(Scope scope, int depth)
    {
        var numIntermediates = 1 + _ctx.Rng.Next(3); // 1–3 intermediates
        var parts = new List<string>();
        for (var i = 0; i < numIntermediates; i++)
            parts.Add(GenIntermediate(scope, depth - 1));

        parts.Add(_exprs.GenInt(scope, depth - 1));
        return $"(begin {string.Join(" ", parts)})";
    }

    private string GenIntermediate(Scope scope, int depth)
    {
        var pick = _ctx.Rng.NextDouble();
        if (pick < 0.5) return _exprs.GenInt(scope, depth);
        if (pick < 0.8) return _exprs.GenBool(scope, depth);
        return _exprs.GenFloat(scope, depth);
    }
}
