namespace ZScheme.Fuzzer.Generation;

// Emits a tuple constructed by `values` and immediately destructured via a `match`
// arm, reducing back to Int. Exercises the `TupleNew` IR path, the `Tuple` pattern
// path, and the per-backend `ValueTuple<T1,T2,...>` codegen.
//
// Invariant: the arm body MUST be Int. Emitting a `(values ...)` form as the arm
// body would leak a tuple out of GenInt, which the diffexec oracle rejects because
// its `Compute()` signature is typed as `Int`.
//
// Per tuple element, patterns are binder or wildcard. Literals are intentionally
// skipped here because a single-arm tuple match with literal patterns is not
// exhaustive — adding a second catchall arm would work, but a single-arm binder/
// wildcard match keeps this generator simple and still exercises the tuple-destruct
// path. Tuple-literal patterns are a good follow-up target for a dedicated generator.
public sealed class TupleExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public TupleExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public string MatchTupleToInt(Scope scope, int depth)
    {
        var arity = 2 + _ctx.Rng.Next(2); // 2 or 3

        var elements = new List<string>();
        for (var i = 0; i < arity; i++)
            elements.Add(_exprs.GenInt(scope, depth - 1));

        var patternParts = new List<string>();
        var armScope = scope;
        var hasBinder = false;
        for (var i = 0; i < arity; i++)
        {
            // 75% fresh binder, 25% wildcard. Ensure at least one binder overall
            // so the arm body has something to reference; otherwise GenInt's leaf
            // variable-picker still works but the decision-tree is trivial.
            var useBinder = _ctx.Rng.NextDouble() < 0.75;
            if (useBinder || (!hasBinder && i == arity - 1))
            {
                var b = _ctx.Fresh();
                patternParts.Add(b);
                armScope = armScope.Extend(b, ExprType.Int);
                hasBinder = true;
            }
            else
            {
                patternParts.Add("_");
            }
        }

        var body = _exprs.GenInt(armScope, depth - 1);
        return $"(match (values {string.Join(" ", elements)}) [(values {string.Join(" ", patternParts)}) {body}])";
    }
}
