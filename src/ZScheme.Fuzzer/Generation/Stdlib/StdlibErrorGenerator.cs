namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over the `stdlib/error` module's `Error` record:
//   * Construct via `(make-error "msg")` — produces an Error with inner = None.
//   * Compose via `(Error "outer" (Some inner))` — chains an inner cause.
//   * Reduce to Int by matching on the inner field: None → 0, (Some _) → 1.
// Requires both `stdlib/error` and `stdlib/option` to be imported (the
// StdlibImportGenerator force-couples the two when error fires).
public sealed class StdlibErrorGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibErrorGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported()
    {
        return _ctx.Imports.Contains(StdlibImport.Error)
            && _ctx.Imports.Contains(StdlibImport.Option);
    }

    // (match (Error/inner <chain>) [None 0] [(Some _) 1])
    public string CauseDepthToInt(Scope scope, int depth)
    {
        var chain = BuildErrorChain(scope, depth);
        return $"(match (Error/inner {chain}) [None 0] [(Some _) 1])";
    }

    // 60% bare `(make-error "msg")`, 40% chained via direct Error ctor with a
    // (Some inner) cause. The chained form constructs the leaf Error via
    // `(make-error "...")` so inner = None at the leaf.
    private string BuildErrorChain(Scope scope, int depth)
    {
        var leafMsg = $"\"err{_ctx.Rng.Next(1000)}\"";
        if (depth <= 0 || _ctx.Rng.NextDouble() < 0.6)
            return $"(make-error {leafMsg})";
        var outerMsg = $"\"outer{_ctx.Rng.Next(1000)}\"";
        var inner = $"(make-error {leafMsg})";
        return $"(Error {outerMsg} (Some {inner}))";
    }
}
