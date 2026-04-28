namespace ZScheme.Fuzzer.Generation;

// Emits `(with-handlers ([ExType v] fallback) ... body-int)`. The body either:
//   (a) raises via `(raise (new System.Exception "fuzz"))` inside an if-false branch
//       so the then-branch preserves the Int type for the body; or
//   (b) naturally throws DivideByZeroException via `(/ n (- y y))`, where `y` is an
//       in-scope Int var (or an Int literal repeated). Roslyn will not constant-fold
//       `y - y` for a runtime variable, so CS0020 (integer division by zero) does
//       not trigger at compile time but DivideByZeroException still fires at runtime.
//
// Multiple handlers (1-3) are emitted per form. The IL backend's handler-region layout
// (`TryEnd = handlerBoundaries[0].Start` in IlEmitter.Emit.cs) fuses all handlers into a
// single try region, while the C# backend emits sequential catch blocks — comparing the
// two under multi-handler dispatch is the whole point of this generator. To keep the
// semantics well-defined, we pick at most one handler per CLR hierarchy chain and place
// `System.Exception` (if selected) last.
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

        string bodyExType;
        string body;

        if (useRaise)
        {
            bodyExType = PickThrownExceptionType();
            var cond = _exprs.GenBool(scope, depth - 1);
            var thenBranch = _exprs.GenInt(scope, depth - 1);
            body = $"(if {cond} {thenBranch} (raise (new {bodyExType} \"fuzz\")))";
        }
        else
        {
            bodyExType = "System.DivideByZeroException";
            var num = _exprs.GenInt(scope, depth - 1);
            var intVars = scope.GetVars(ExprType.Int);
            var y = intVars[_ctx.Rng.Next(intVars.Count)];
            body = $"(/ {num} (- {y} {y}))";
        }

        // Choose 1-3 handler types and render their `([Type var] fallback)` clauses.
        var handlerClauses = BuildHandlerClauses(bodyExType, scope, depth);

        return $"(with-handlers {string.Join(" ", handlerClauses)} {body})";
    }

    // Picks 1-3 handler types whose chain covers `thrownType` and renders each as
    // a complete `([Type var] fallback)` clause with a fresh binder name and an
    // Int-typed fallback expression. Exposed so AsyncExprGenerator can wrap awaits
    // in `with-handlers` without duplicating the chain-selection logic that keeps
    // System.Exception last and avoids unreachable-handler diagnostics.
    public List<string> BuildHandlerClauses(string thrownType, Scope scope, int depth)
    {
        var handlerTypes = PickHandlerTypes(thrownType);
        var clauses = new List<string>(handlerTypes.Count);
        foreach (var exType in handlerTypes)
        {
            var handlerVar = _ctx.Fresh();
            var fallback = _exprs.GenInt(scope, depth - 1);
            clauses.Add($"([{exType} {handlerVar}] {fallback})");
        }
        return clauses;
    }

    // Handler-type picker. Groups represent disjoint hierarchy chains so picking
    // one per group keeps most-derived-first ordering trivial (unrelated siblings
    // can appear in any order). `System.Exception` is the base of all — if chosen,
    // it goes last. The body's thrown type is always represented by its own chain
    // so the emitted handlers match at least once.
    private List<string> PickHandlerTypes(string bodyExType)
    {
        // Chain groups: index 0 is the most-derived type in that chain.
        string[][] chains =
        [
            ["System.DivideByZeroException", "System.ArithmeticException"],
            ["System.InvalidOperationException"],
            ["System.ArgumentException"],
        ];

        var picks = new List<string>();

        // Always include a catcher for the thrown type. If the thrown type is one
        // of the chain leaves, pick the leaf itself or a base in its chain.
        var bodyChainIdx = -1;
        for (var i = 0; i < chains.Length; i++)
        {
            if (Array.IndexOf(chains[i], bodyExType) >= 0)
            {
                bodyChainIdx = i;
                break;
            }
        }
        // Track whether System.Exception is needed as the trailing base catcher.
        // It must never appear anywhere but last — otherwise subsequent subtype
        // handlers are unreachable and the frontend (matching the C# backend)
        // rejects the program with a "handler is unreachable" diagnostic.
        var needBaseException = false;

        if (bodyChainIdx >= 0)
        {
            // 70% pick the leaf (exact match), 30% pick a base type in the chain.
            var chain = chains[bodyChainIdx];
            var idx = chain.Length > 1 && _ctx.Rng.NextDouble() < 0.3
                ? 1 + _ctx.Rng.Next(chain.Length - 1)
                : 0;
            picks.Add(chain[idx]);
        }
        else if (bodyExType == "System.Exception")
        {
            // Body throws raw Exception; defer adding the catcher until after
            // any specific-chain picks so it stays last.
            needBaseException = true;
        }
        else
        {
            picks.Add(bodyExType);
        }

        // Add 0-2 additional handlers from other chains.
        var extra = _ctx.Rng.Next(3); // 0, 1, or 2
        var remainingChains = Enumerable.Range(0, chains.Length)
            .Where(i => i != bodyChainIdx)
            .ToList();
        Shuffle(remainingChains);
        for (var i = 0; i < extra && i < remainingChains.Count; i++)
        {
            var chain = chains[remainingChains[i]];
            var idx = chain.Length > 1 && _ctx.Rng.NextDouble() < 0.3
                ? 1 + _ctx.Rng.Next(chain.Length - 1)
                : 0;
            picks.Add(chain[idx]);
        }

        // 25% chance to also add System.Exception — must go last.
        if (_ctx.Rng.NextDouble() < 0.25) needBaseException = true;

        // Order rule: unrelated chain picks may appear in any order; System.Exception,
        // if present, must be last. Shuffle only the non-base picks.
        Shuffle(picks);
        if (needBaseException) picks.Add("System.Exception");

        return picks;
    }

    private string PickThrownExceptionType()
    {
        var options = new[]
        {
            "System.DivideByZeroException",
            "System.InvalidOperationException",
            "System.ArgumentException",
            "System.Exception",
        };
        return options[_ctx.Rng.Next(options.Length)];
    }

    private void Shuffle<T>(List<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _ctx.Rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
