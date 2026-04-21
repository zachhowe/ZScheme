namespace ZScheme.Fuzzer.Generation;

// Emits a tuple constructed by `values` and immediately destructured via a `match`
// arm, reducing back to Int. Exercises the `TupleNew` IR path, the `Tuple` pattern
// path, and the per-backend `ValueTuple<T1,T2,...>` codegen.
//
// Invariant: the arm body MUST be Int. Emitting a `(values ...)` form as the arm
// body would leak a tuple out of GenInt, which the diffexec oracle rejects because
// its `Compute()` signature is typed as `Int`.
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

        var binders = new List<string>();
        var armScope = scope;
        for (var i = 0; i < arity; i++)
        {
            var b = _ctx.Fresh();
            binders.Add(b);
            armScope = armScope.Extend(b, ExprType.Int);
        }

        var body = _exprs.GenInt(armScope, depth - 1);
        return $"(match (values {string.Join(" ", elements)}) [(values {string.Join(" ", binders)}) {body}])";
    }
}
