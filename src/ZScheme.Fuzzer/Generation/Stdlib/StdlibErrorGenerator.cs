namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over the `stdlib/error` module's `ErrorInfo` record:
//   * Construct via `(Error "msg")` — produces an ErrorInfo with cause = None.
//   * Compose via `(ErrorInfo "outer" (Some inner))` — chains a cause.
//   * Reduce to Int by matching on the cause field: None → 0, (Some _) → 1.
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

    public bool IsImported() =>
        _ctx.Imports.Contains(StdlibImport.Error)
        && _ctx.Imports.Contains(StdlibImport.Option);

    // (match (ErrorInfo/cause <chain>) [None 0] [(Some _) 1])
    public string CauseDepthToInt(Scope scope, int depth)
    {
        var chain = BuildErrorChain(scope, depth);
        return $"(match (ErrorInfo/cause {chain}) [None 0] [(Some _) 1])";
    }

    // 60% bare `(Error "msg")`, 40% chained via direct ErrorInfo ctor with a
    // (Some inner) cause. The chained form constructs the inner ErrorInfo via
    // `(Error "...")` so cause = None at the leaf.
    private string BuildErrorChain(Scope scope, int depth)
    {
        var leafMsg = $"\"err{_ctx.Rng.Next(1000)}\"";
        if (depth <= 0 || _ctx.Rng.NextDouble() < 0.6)
            return $"(Error {leafMsg})";
        var outerMsg = $"\"outer{_ctx.Rng.Next(1000)}\"";
        var inner = $"(Error {leafMsg})";
        return $"(ErrorInfo {outerMsg} (Some {inner}))";
    }
}
