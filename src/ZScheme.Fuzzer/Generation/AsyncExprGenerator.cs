namespace ZScheme.Fuzzer.Generation;

// Generates Int-typed expressions valid inside an async function body. Owns the
// emission of `(await ...)` — no other generator emits `await`, which preserves
// the type-inference invariant that `await` only appears in async context.
//
// Sub-expressions of an await call are produced via the regular `ExprGenerator`
// (so e.g. an awaited int arg can still use match/let/etc. without containing
// further awaits). Recursive descent through if/let/match keeps awaits reachable
// inside arm bodies — that's where the IL state-machine analyzer hoists locals
// and where C# / IL backends most often disagree.
public sealed class AsyncExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;
    private readonly ExceptionExprGenerator _exception;

    public AsyncExprGenerator(GeneratorContext ctx, ExprGenerator exprs, ExceptionExprGenerator exception)
    {
        _ctx = ctx;
        _exprs = exprs;
        _exception = exception;
    }

    public string GenAsyncBodyInt(Scope scope, int depth)
    {
        var asyncFuncs = _ctx.AsyncUserFuncs.ToList();
        if (depth <= 0 || asyncFuncs.Count == 0)
            return _exprs.GenInt(scope, depth);

        var weights = new List<(int Weight, Func<string> Gen)>
        {
            (4, () => GenAwait(asyncFuncs, scope, depth)),
            (2, () => GenAwaitWithHandlers(asyncFuncs, scope, depth)),
            (2, () => GenAwaitLet(asyncFuncs, scope, depth)),
            (2, () => GenAwaitIf(asyncFuncs, scope, depth)),
            (3, () => _exprs.GenInt(scope, depth - 1)),
        };
        return _ctx.PickWeighted(weights)();
    }

    private string GenAwait(List<UserFunc> asyncFuncs, Scope scope, int depth)
    {
        var callee = asyncFuncs[_ctx.Rng.Next(asyncFuncs.Count)];
        var args = new List<string>(callee.ParamTypes.Count);
        foreach (var _ in callee.ParamTypes)
            args.Add(_exprs.GenInt(scope, depth - 1));
        var call = args.Count == 0
            ? $"({callee.Name})"
            : $"({callee.Name} {string.Join(" ", args)})";
        return $"(await {call})";
    }

    // Wraps an await in `with-handlers`. We don't know which async callees throw
    // (they're picked at random), so handlers always include System.Exception as
    // a base catcher via ExceptionExprGenerator.BuildHandlerClauses.
    private string GenAwaitWithHandlers(List<UserFunc> asyncFuncs, Scope scope, int depth)
    {
        var awaitExpr = GenAwait(asyncFuncs, scope, depth - 1);
        var clauses = _exception.BuildHandlerClauses("System.Exception", scope, depth - 1);
        return $"(with-handlers {string.Join(" ", clauses)} {awaitExpr})";
    }

    private string GenAwaitLet(List<UserFunc> asyncFuncs, Scope scope, int depth)
    {
        var name = _ctx.Fresh();
        var value = GenAwait(asyncFuncs, scope, depth - 1);
        var childScope = scope.Extend(name, ExprType.Int);
        var body = GenAsyncBodyInt(childScope, depth - 1);
        return $"(let [{name} {value}] {body})";
    }

    private string GenAwaitIf(List<UserFunc> asyncFuncs, Scope scope, int depth)
    {
        _ = asyncFuncs;
        var cond = _exprs.GenBool(scope, depth - 1);
        var t = GenAsyncBodyInt(scope, depth - 1);
        var e = GenAsyncBodyInt(scope, depth - 1);
        return $"(if {cond} {t} {e})";
    }
}
