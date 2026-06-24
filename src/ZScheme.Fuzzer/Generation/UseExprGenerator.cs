namespace ZScheme.Fuzzer.Generation;

// Emits `(use ([x (new <Disposable>)]) body)` and `(use* (...) body)` shaped Int
// expressions, exercising the deterministic-disposal special forms (commits 2de1c09 /
// 608fb97). These lower to a try/finally that disposes the resource on scope exit —
// the C# backend emits a native `using`, the IL backend emits an explicit try/finally
// (and, for async bodies, a state-guarded finally). That divergence is exactly what the
// differential oracles should be comparing.
//
// The resource binding is intentionally never referenced from the body: the fuzzer's
// Scope only tracks Int/Bool/Float/String, whereas the resource is a CLR object, so we
// keep it out of scope and let the body be a normal Int expression. Disposal still runs
// regardless of whether the body uses the binding.
//
// Resources are constructed with a parameterless `(new ...)` over framework types that
// implement IDisposable, so no `import-clr` block is required.
public sealed class UseExprGenerator
{
    // Framework types that are IDisposable and constructible via parameterless `(new ...)`.
    // Shared with AsyncExprGenerator so the async-use reducers draw from the same pool.
    public static readonly string[] DisposableTypes =
    [
        "System.IO.MemoryStream",
        "System.IO.StringWriter",
        "System.Threading.CancellationTokenSource",
    ];

    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public UseExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Picks a disposable type at random for use as a `(new ...)` resource.
    public string PickDisposable()
    {
        return DisposableTypes[_ctx.Rng.Next(DisposableTypes.Length)];
    }

    // `(use ([x (new <Disposable>)]) <int body>)`. ~30% of the time emits the
    // type-annotated upcast variant `(use ([s : System.IO.Stream (new System.IO.MemoryStream)]) ...)`
    // so the annotation/upcast path in InferUse is exercised.
    public string UseToInt(Scope scope, int depth)
    {
        var name = _ctx.Fresh();
        var body = _exprs.GenInt(scope, depth - 1);

        if (_ctx.Rng.NextDouble() < 0.30)
            return $"(use ([{name} : System.IO.Stream (new System.IO.MemoryStream)]) {body})";

        return $"(use ([{name} (new {PickDisposable()})]) {body})";
    }

    // `(use* ([a (new ...)] [b (new ...)] ...) <int body>)` with 2-3 resources, each a
    // random disposable type. use* desugars to nested `use` forms disposed in reverse
    // binding order.
    public string UseStarToInt(Scope scope, int depth)
    {
        var numBindings = 2 + _ctx.Rng.Next(2); // 2 or 3
        var bindings = new List<string>(numBindings);
        for (var i = 0; i < numBindings; i++)
            bindings.Add($"[{_ctx.Fresh()} (new {PickDisposable()})]");

        var body = _exprs.GenInt(scope, depth - 1);
        return $"(use* ({string.Join(" ", bindings)}) {body})";
    }

    // `(with-handlers ([System.Exception v] <fallback>) (use ([m (new ...)]) <throwing body>))`.
    // The use body throws via a predicate-guarded raise (the `if`'s then-branch keeps the
    // body Int-typed); the resource must still be disposed as the exception unwinds through
    // the finally, and the handler catches so the whole expression stays a well-typed Int.
    public string UseDisposeOnThrowToInt(Scope scope, int depth)
    {
        var handlerVar = _ctx.Fresh();
        var fallback = _exprs.GenInt(scope, depth - 1);
        var resourceVar = _ctx.Fresh();
        var cond = _exprs.GenBool(scope, depth - 1);
        var thenBranch = _exprs.GenInt(scope, depth - 1);
        var body =
            $"(if {cond} {thenBranch} (raise (new System.InvalidOperationException \"fuzz\")))";
        var use = $"(use ([{resourceVar} (new {PickDisposable()})]) {body})";
        return $"(with-handlers ([System.Exception {handlerVar}] {fallback}) {use})";
    }
}
