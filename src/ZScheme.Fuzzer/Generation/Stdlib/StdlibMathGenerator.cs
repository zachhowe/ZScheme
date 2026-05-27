using System.Globalization;

namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over stdlib/math exports. Emits only the bindings whose
// CLR overload is unambiguous out of the box:
//   * sqrt, floor, ceiling — bound to (Double -> Double) by default resolution
//   * maxf, minf — bound to (Float Float -> Float) via explicit annotation in math.zs
// The numeric `abs`, `min`, `max` exports are skipped because their CLR-default
// overload resolution targets sbyte/Byte (same root cause as ClrBinding.MathAbsInt
// requiring an explicit annotation in ClrInteropExprGenerator).
public sealed class StdlibMathGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibMathGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported()
    {
        return _ctx.Imports.Contains(StdlibImport.Math);
    }

    // (double->float (sqrt (float->double <float>))) — chain through Double.
    // Sqrt of negative is NaN; we wrap input in (abs ...) via a positive literal
    // path. Use a non-negative numeric literal floor to keep DiffExec deterministic.
    public string SqrtToFloat(Scope scope, int depth)
    {
        var inner = NonNegativeFloat(scope, depth - 1);
        return $"(double->float (sqrt (float->double {inner})))";
    }

    // (double->float (floor (float->double <float>)))
    public string FloorToFloat(Scope scope, int depth)
    {
        var inner = _exprs.GenFloat(scope, depth - 1);
        return $"(double->float (floor (float->double {inner})))";
    }

    // (double->float (ceiling (float->double <float>)))
    public string CeilingToFloat(Scope scope, int depth)
    {
        var inner = _exprs.GenFloat(scope, depth - 1);
        return $"(double->float (ceiling (float->double {inner})))";
    }

    // (maxf a b) — Float Float -> Float
    public string MaxfToFloat(Scope scope, int depth)
    {
        var a = _exprs.GenFloat(scope, depth - 1);
        var b = _exprs.GenFloat(scope, depth - 1);
        return $"(maxf {a} {b})";
    }

    // (minf a b) — Float Float -> Float
    public string MinfToFloat(Scope scope, int depth)
    {
        var a = _exprs.GenFloat(scope, depth - 1);
        var b = _exprs.GenFloat(scope, depth - 1);
        return $"(minf {a} {b})";
    }

    // Non-negative Float input for sqrt: a small positive literal so the result
    // is well-defined across both backends. We deliberately do not use GenFloat
    // recursively because that path can yield NaN/Inf which sqrt would propagate
    // and which DiffExec equality treatment is not tuned for in this milestone.
    private string NonNegativeFloat(Scope scope, int depth)
    {
        // 70% small positive integral float, 30% positive randomized.
        if (_ctx.Rng.NextDouble() < 0.7)
        {
            var n = _ctx.Rng.Next(0, 100);
            return $"{n}.0";
        }

        var v = _ctx.Rng.NextDouble() * 1000.0;
        var s = v.ToString("G7", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E'))
            s += ".0";
        return s;
    }
}
