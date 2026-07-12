namespace ZScheme.Fuzzer.Generation;

// Exercises the six built-in numeric/string conversion functions defined in
// Compiler/Types/TypeEnv.cs:
//
//   int->string : Int    -> String
//   string->int : String -> Int       (raises on non-numeric input)
//   int->float  : Int    -> Float
//   float->int  : Float  -> Int       (already used by other generators)
//   double->float : Double -> Float   (already used)
//   float->double : Float  -> Double  (already used)
//
// These are compiler primitives (no import required) so this generator is
// always available. Reducers cover both the direct conversions and the round
// trips, which exercise the lowering path on each backend twice per call.
//
// `string->int` is only emitted on a string we just produced via int->string,
// so the integer round-trip is well-defined and DiffExec doesn't need to
// reconcile FormatException semantics across backends.
public sealed class ConversionExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public ConversionExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // (string->int (int->string n)) — round-trip yields n.
    public string IntStringRoundTripToInt(Scope scope, int depth)
    {
        var n = _exprs.GenInt(scope, depth - 1);
        return $"(string->int (int->string {n}))";
    }

    // (float->int (int->float n)) — round-trip yields n for representable Int.
    // We pick a small range so float64 can represent it exactly.
    public string IntFloatRoundTripToInt(Scope scope, int depth)
    {
        var n = _ctx.Rng.Next(-1000000, 1000001);
        return $"(float->int (int->float {n}))";
    }

    // (int->float n) — direct Float reducer, no round trip.
    public string IntToFloatDirect(Scope scope, int depth)
    {
        var n = _exprs.GenInt(scope, depth - 1);
        return $"(int->float {n})";
    }

    // (double->float (float->double f)) — round trips through Double.
    public string FloatDoubleRoundTripToFloat(Scope scope, int depth)
    {
        var f = _exprs.GenFloat(scope, depth - 1);
        return $"(double->float (float->double {f}))";
    }

    // (if (= (float->double a) (float->double b)) t e) — polymorphic equality
    // instantiated at Double, the only built-in operator reachable at that
    // type. ~50% compares a value against its own widening so the equal branch
    // is well represented.
    public string DoubleEqToInt(Scope scope, int depth)
    {
        var a = _exprs.GenFloat(scope, depth - 1);
        var b = _ctx.Rng.NextDouble() < 0.5 ? a : _exprs.GenFloat(scope, depth - 1);
        var op = _ctx.Rng.NextDouble() < 0.5 ? "=" : "!=";
        var t = _exprs.GenInt(scope, depth - 1);
        var e = _exprs.GenInt(scope, depth - 1);
        return $"(if ({op} (float->double {a}) (float->double {b})) {t} {e})";
    }
}
