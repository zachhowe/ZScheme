namespace ZScheme.Fuzzer.Generation;

// Emits `(with-handlers ([ExType v] fallback-int) body-int)`. The body either:
//   (a) raises via `(raise (new System.Exception "fuzz"))` inside an if-false branch
//       so the then-branch preserves the Int type for the body; or
//   (b) naturally throws DivideByZeroException via `(/ n (- y y))`, where `y` is an
//       in-scope Int var (or an Int literal repeated). Roslyn will not constant-fold
//       `y - y` for a runtime variable, so CS0020 (integer division by zero) does
//       not trigger at compile time but DivideByZeroException still fires at runtime.
//
// Only one handler per `with-handlers` — the IL backend's multi-handler region
// layout (`TryEnd = handlerBoundaries[0].Start` in IlEmitter.Emit.cs) makes
// most-derived-first ordering load-bearing, and emitting a single handler sidesteps
// that class of divergence.
public sealed class ExceptionExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public ExceptionExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public string WithHandlersToInt(Scope scope, int depth)
    {
        // The natural-throw variant needs an in-scope Int var so we can build
        // `(- y y)` — a literal-based `(- 5 5)` is constant-folded by Roslyn and
        // triggers CS0020 at Roslyn compile time.
        var canNaturalThrow = scope.GetVars(ExprType.Int).Count > 0;
        var useRaise = !canNaturalThrow || _ctx.Rng.NextDouble() < 0.5;

        var fallback = _exprs.GenInt(scope, depth - 1);
        var handlerVar = _ctx.Fresh();

        string exType;
        string body;

        if (useRaise)
        {
            exType = PickExceptionType();
            var cond = _exprs.GenBool(scope, depth - 1);
            var thenBranch = _exprs.GenInt(scope, depth - 1);
            body = $"(if {cond} {thenBranch} (raise (new {exType} \"fuzz\")))";
        }
        else
        {
            exType = "System.DivideByZeroException";
            var num = _exprs.GenInt(scope, depth - 1);
            var intVars = scope.GetVars(ExprType.Int);
            var y = intVars[_ctx.Rng.Next(intVars.Count)];
            body = $"(/ {num} (- {y} {y}))";
        }

        return $"(with-handlers ([{exType} {handlerVar}] {fallback}) {body})";
    }

    private string PickExceptionType()
    {
        // Leaving most-derived types first is a no-op with a single handler, but
        // the ordering matters if multi-handler support is ever added here.
        var options = new[]
        {
            "System.DivideByZeroException",
            "System.InvalidOperationException",
            "System.ArgumentException",
            "System.Exception",
        };
        return options[_ctx.Rng.Next(options.Length)];
    }
}
