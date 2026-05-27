using System.Globalization;

namespace ZScheme.Fuzzer.Generation;

// Emits a tuple constructed by `values` and immediately destructured via a `match`
// arm, reducing back to Int. Exercises the `TupleNew` IR path, the `Tuple` pattern
// path, and the per-backend `ValueTuple<T1,T2,...>` codegen.
//
// Invariant: the arm body MUST be Int. Emitting a `(values ...)` form as the arm
// body would leak a tuple out of GenInt, which the diffexec oracle rejects because
// its `Compute()` signature is typed as `Int`.
//
// Per tuple element, patterns are binder, wildcard, or Int literal. When a literal
// pattern appears the match is no longer exhaustive — a terminal `[_ fallback]`
// arm is appended in that case.
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
        var hasLiteral = false;
        for (var i = 0; i < arity; i++)
        {
            // 60% fresh binder, 25% wildcard, 15% Int literal. Ensure at least one
            // binder overall so the arm body has something non-trivial to reference.
            var forceBinder = !hasBinder && i == arity - 1;
            var roll = forceBinder ? 0.0 : _ctx.Rng.NextDouble();
            if (roll < 0.60)
            {
                var b = _ctx.Fresh();
                patternParts.Add(b);
                armScope = armScope.Extend(b, ExprType.Int);
                hasBinder = true;
            }
            else if (roll < 0.85)
            {
                patternParts.Add("_");
            }
            else
            {
                var lit = _ctx.Rng.Next(-2, 5);
                patternParts.Add(lit.ToString(CultureInfo.InvariantCulture));
                hasLiteral = true;
            }
        }

        var body = _exprs.GenInt(armScope, depth - 1);
        var mainArm = $"[(values {string.Join(" ", patternParts)}) {body}]";
        var scrutinee = $"(values {string.Join(" ", elements)})";

        if (hasLiteral)
        {
            var fallback = _exprs.GenInt(scope, depth - 1);
            return $"(match {scrutinee} {mainArm} [_ {fallback}])";
        }

        return $"(match {scrutinee} {mainArm})";
    }

    // Emits a mixed-ground tuple `(values <int> <float>)` and destructures it.
    // Exercises the ValueTuple<Int,Float> codegen path — distinct from the
    // homogeneous-Int tuples above.
    public string MatchMixedTupleToInt(Scope scope, int depth)
    {
        var eInt = _exprs.GenInt(scope, depth - 1);
        var eFloat = _exprs.GenFloat(scope, depth - 1);

        var hasBinder = false;
        var hasLiteral = false;
        var armScope = scope;

        string PatternFor(ExprType slotType, bool forceBinder)
        {
            var roll = forceBinder ? 0.0 : _ctx.Rng.NextDouble();
            if (roll < 0.50)
            {
                var b = _ctx.Fresh();
                armScope = armScope.Extend(b, slotType);
                hasBinder = true;
                return b;
            }

            if (roll < 0.80) return "_";
            hasLiteral = true;
            if (slotType == ExprType.Int)
                return _ctx.Rng.Next(-2, 5).ToString(CultureInfo.InvariantCulture);
            // Float literal — small set of deterministic values.
            var floatPool = new[] { "0.0", "-0.0", "1.0", "-1.0" };
            return floatPool[_ctx.Rng.Next(floatPool.Length)];
        }

        var p1 = PatternFor(ExprType.Int, false);
        var p2 = PatternFor(ExprType.Float, !hasBinder);

        var body = _exprs.GenInt(armScope, depth - 1);
        var scrutinee = $"(values {eInt} {eFloat})";
        var mainArm = $"[(values {p1} {p2}) {body}]";

        if (hasLiteral)
        {
            var fallback = _exprs.GenInt(scope, depth - 1);
            return $"(match {scrutinee} {mainArm} [_ {fallback}])";
        }

        return $"(match {scrutinee} {mainArm})";
    }
}
