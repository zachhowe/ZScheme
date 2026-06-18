namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over stdlib/string exports: format, equals?, empty?,
// starts-with?, ends-with?. All return Bool or String — the Int reducers wrap
// them in `(if ... 1 0)` chains; the Bool reducers feed directly into the
// GenBool weight table.
public sealed class StdlibStringGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibStringGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported()
    {
        return _ctx.Imports.Contains(StdlibImport.String);
    }

    // (equals? s1 s2)
    public string EqualsPredicateToBool(Scope scope, int depth)
    {
        var a = _exprs.GenString(scope, depth - 1);
        var b = _exprs.GenString(scope, depth - 1);
        return $"(equals? {a} {b})";
    }

    // (empty? s)
    public string EmptyPredicateToBool(Scope scope, int depth)
    {
        var a = _exprs.GenString(scope, depth - 1);
        return $"(empty? {a})";
    }

    // (starts-with? s prefix)
    public string StartsWithPredicateToBool(Scope scope, int depth)
    {
        var s = _exprs.GenString(scope, depth - 1);
        var prefix = _exprs.GenString(scope, depth - 1);
        return $"(starts-with? {s} {prefix})";
    }

    // (ends-with? s suffix)
    public string EndsWithPredicateToBool(Scope scope, int depth)
    {
        var s = _exprs.GenString(scope, depth - 1);
        var suffix = _exprs.GenString(scope, depth - 1);
        return $"(ends-with? {s} {suffix})";
    }

    // (if (empty? (format "{0}{1}" a b)) 1 0)
    // Uses 1- or 2-substitution format strings with String args. The `args` param
    // is variadic `String ...` — at the call site we pass the args directly.
    public string FormatEmptyToInt(Scope scope, int depth)
    {
        var twoArgs = _ctx.Rng.NextDouble() < 0.5;
        var fmt = twoArgs ? "\"{0}_{1}\"" : "\"x{0}\"";
        var a = _exprs.GenString(scope, depth - 1);
        var args = twoArgs
            ? $"{a} {_exprs.GenString(scope, depth - 1)}"
            : a;
        return $"(if (empty? (format {fmt} {args})) 1 0)";
    }
}
