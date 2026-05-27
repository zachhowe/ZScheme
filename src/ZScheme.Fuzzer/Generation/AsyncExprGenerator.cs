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
    private readonly ExceptionExprGenerator _exception;
    private readonly ExprGenerator _exprs;

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
            (2, () => GenAwaitMatch(asyncFuncs, scope, depth)),
            (2, () => GenAwaitBegin(asyncFuncs, scope, depth)),
            (2, () => GenAwaitInHandlerBody(asyncFuncs, scope, depth)),
            (2, () => GenAwaitNested(asyncFuncs, scope, depth)),
            (3, () => _exprs.GenInt(scope, depth - 1))
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

    // Lifts an `(await ...)` into a match arm body so the IL state-machine
    // analyzer must hoist locals across pattern decision-tree branches. One
    // arm contains the await; the other arms are plain Int bodies. Match has
    // 2 literal arms + terminal wildcard (always exhaustive).
    private string GenAwaitMatch(List<UserFunc> asyncFuncs, Scope scope, int depth)
    {
        var scrutinee = _exprs.GenInt(scope, depth - 1);
        var lit1 = _ctx.Rng.Next(-2, 5);
        int lit2;
        do
        {
            lit2 = _ctx.Rng.Next(-2, 5);
        } while (lit2 == lit1);

        var awaitArmIdx = _ctx.Rng.Next(3);

        string Body(int i)
        {
            return i == awaitArmIdx
                ? GenAwait(asyncFuncs, scope, depth - 1)
                : _exprs.GenInt(scope, depth - 1);
        }

        return $"(match {scrutinee} [{lit1} {Body(0)}] [{lit2} {Body(1)}] [_ {Body(2)}])";
    }

    // Sequences an `(await ...)` whose result is discarded, followed by an Int
    // tail. Both surface forms desugar to `(let [_ (await ...)] tail)` — the
    // explicit underscore-let path is what commit 7d94c2c fixed (the async
    // state-machine analyzer skipping hoisting of underscore lets).
    private string GenAwaitBegin(List<UserFunc> asyncFuncs, Scope scope, int depth)
    {
        var awaitExpr = GenAwait(asyncFuncs, scope, depth - 1);
        var tail = GenAsyncBodyInt(scope, depth - 1);
        if (_ctx.Rng.NextDouble() < 0.5)
            return $"(begin {awaitExpr} {tail})";
        return $"(let [_ {awaitExpr}] {tail})";
    }

    // Places an `(await ...)` inside a `with-handlers` HANDLER body (not the
    // protected body — that's GenAwaitWithHandlers). The protected body raises
    // unconditionally so the handler is reached, exercising the
    // WithHandlersHoister path for handler-body awaits in async state machines.
    private string GenAwaitInHandlerBody(List<UserFunc> asyncFuncs, Scope scope, int depth)
    {
        var exType = "System.Exception";
        var handlerVar = _ctx.Fresh();
        var awaitInHandler = GenAwait(asyncFuncs, scope, depth - 1);
        var cond = _exprs.GenBool(scope, depth - 1);
        var thenBranch = _exprs.GenInt(scope, depth - 1);
        var body = $"(if {cond} {thenBranch} (raise (new {exType} \"fuzz\")))";
        return $"(with-handlers ([{exType} {handlerVar}] {awaitInHandler}) {body})";
    }

    // Two awaits in one expression tree: `(await (callee (await inner) ...))`.
    // The outer await spills the result of the inner await into the state
    // machine before the outer call frame is built. Falls back to a plain
    // GenAwait when no async helper takes parameters.
    private string GenAwaitNested(List<UserFunc> asyncFuncs, Scope scope, int depth)
    {
        var withParams = asyncFuncs.Where(f => f.ParamTypes.Count > 0).ToList();
        if (withParams.Count == 0)
            return GenAwait(asyncFuncs, scope, depth - 1);

        var outer = withParams[_ctx.Rng.Next(withParams.Count)];
        var nestedArgIdx = _ctx.Rng.Next(outer.ParamTypes.Count);
        var args = new List<string>(outer.ParamTypes.Count);
        for (var i = 0; i < outer.ParamTypes.Count; i++)
            args.Add(i == nestedArgIdx
                ? GenAwait(asyncFuncs, scope, depth - 1)
                : _exprs.GenInt(scope, depth - 1));
        var call = $"({outer.Name} {string.Join(" ", args)})";
        return $"(await {call})";
    }
}
