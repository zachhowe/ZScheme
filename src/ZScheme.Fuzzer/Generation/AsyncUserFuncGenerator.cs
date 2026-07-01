namespace ZScheme.Fuzzer.Generation;

// Emits `(define-async (gN ...) : (Task Int) <body>)` whose bodies exercise the
// state-machine codegen across both backends. Body shapes:
//   * regular: an Int body that may include awaits of earlier async funcs (via
//     AsyncExprGenerator).
//   * throwing-divide: `(/ <int> (- y y))` over an Int param — surfaces a
//     DivideByZeroException through a faulted Task<int>.
//   * throwing-raise: predicate-guarded `(raise (new System.InvalidOperationException ...))`.
//   * with-handlers-internal: `(with-handlers [...] (await (g_other ...)))` — catches
//     a faulted task from a previously-defined async helper.
//
// All async funcs return Task<Int>. They go into `_ctx.UserFuncs` with IsAsync=true
// so sync call sites (GenCall, PartialExprGenerator) filter them out via
// `_ctx.SyncUserFuncs`.
public sealed class AsyncUserFuncGenerator
{
    private static readonly IReadOnlySet<ExprType> OnlyInt = new HashSet<ExprType> { ExprType.Int };
    private readonly AsyncExprGenerator _async;
    private readonly GeneratorContext _ctx;
    private readonly ExceptionExprGenerator _exception;
    private readonly ExprGenerator _exprs;

    public AsyncUserFuncGenerator(
        GeneratorContext ctx,
        ExprGenerator exprs,
        AsyncExprGenerator async,
        ExceptionExprGenerator exception
    )
    {
        _ctx = ctx;
        _exprs = exprs;
        _async = async;
        _exception = exception;
    }

    public UserFunc GenerateAsyncFunction(string name)
    {
        var arity = _ctx.Rng.Next(3); // 0, 1, or 2
        var paramNames = new List<string>(arity);
        var scope = new Scope();
        for (var i = 0; i < arity; i++)
        {
            var p = _ctx.Fresh();
            paramNames.Add(p);
            scope = scope.Extend(p, ExprType.Int);
        }

        var sigParams = paramNames.Select(p => $"[{p} : Int]");
        var sig = paramNames.Count == 0 ? $"({name})" : $"({name} {string.Join(" ", sigParams)})";

        var hasParam = arity > 0;
        var hasPriorAsync = _ctx.AsyncUserFuncs.Any();

        var pick = _ctx.Rng.NextDouble();
        string body;
        if (pick < 0.50)
        {
            body = _async.GenAsyncBodyInt(scope, _ctx.MaxDepth);
        }
        else if (pick < 0.75 && hasParam)
        {
            // Throwing-divide: y is a runtime variable so Roslyn can't constant-fold
            // the (- y y) and the runtime DivideByZeroException fires inside the
            // state machine, surfacing as a faulted Task<int>.
            var y = paramNames[_ctx.Rng.Next(arity)];
            var num = _exprs.GenInt(scope, _ctx.MaxDepth - 1);
            body = $"(/ {num} (- {y} {y}))";
        }
        else if (pick < 0.90 && hasParam)
        {
            // Throwing-raise: predicate-guarded so the type-checker still infers Int.
            var y = paramNames[_ctx.Rng.Next(arity)];
            body = $"(if (> {y} 0) {y} (raise (new System.InvalidOperationException \"fuzz\")))";
        }
        else if (hasPriorAsync)
        {
            var callees = _ctx.AsyncUserFuncs.ToList();
            var callee = callees[_ctx.Rng.Next(callees.Count)];
            var args = new List<string>(callee.ParamTypes.Count);
            foreach (var _ in callee.ParamTypes)
                args.Add(_exprs.GenInt(scope, _ctx.MaxDepth - 1));
            var awaitExpr =
                args.Count == 0
                    ? $"(await ({callee.Name}))"
                    : $"(await ({callee.Name} {string.Join(" ", args)}))";
            var clauses = _exception.BuildHandlerClauses(
                "System.Exception",
                scope,
                _ctx.MaxDepth - 1
            );
            body = $"(with-handlers {string.Join(" ", clauses)} {awaitExpr})";
        }
        else
        {
            // Fallback: regular body when arity / prior-async preconditions don't hold.
            body = _async.GenAsyncBodyInt(scope, _ctx.MaxDepth);
        }

        var def = $"(define-async {sig} : (Task Int)\n  {body})";
        var paramTypes = Enumerable.Repeat(ExprType.Int, arity).ToList();
        var isGeneric = new bool[arity];
        return new UserFunc(
            name,
            UserFuncKind.Regular,
            paramTypes,
            def,
            OnlyInt,
            isGeneric,
            false,
            true
        );
    }
}
