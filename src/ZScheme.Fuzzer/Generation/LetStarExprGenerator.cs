namespace ZScheme.Fuzzer.Generation;

// Emits `(let* ([x v1] [y v2] [z v3]) body)` shaped Int expressions.
//
// `let*` lowers differently from a flat `let`: each binding is in scope for
// every later binding's RHS, which exercises the sequential-scope desugaring
// path in AstBuilder (BuildLetStar) and the resulting nested-let IR. Later
// bindings reference earlier ones with ~50% probability per slot so the
// inter-binding dependency is exercised without forcing it (a chain of
// independent bindings should also lower correctly).
//
// All bindings are Int-typed for simplicity; mixed-type let* would require
// shadowing-aware scope plumbing that flat-let doesn't currently need.
public sealed class LetStarExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public LetStarExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public string LetStarToInt(Scope scope, int depth)
    {
        return GenLetStar(ExprType.Int, scope, depth);
    }

    public string LetStarToBool(Scope scope, int depth)
    {
        return GenLetStar(ExprType.Bool, scope, depth);
    }

    private string GenLetStar(ExprType resultType, Scope scope, int depth)
    {
        var numBindings = 2 + _ctx.Rng.Next(2); // 2 or 3
        var names = new List<string>(numBindings);
        var bindings = new List<string>(numBindings);
        var bindScope = scope;

        for (var i = 0; i < numBindings; i++)
        {
            // FreshOrShadow over bindScope enables the high-value intra-let*
            // case: a later binding rebinding an earlier name while its RHS
            // still sees the shadowed value.
            var name = _ctx.FreshOrShadow(bindScope, ExprType.Int);
            // Each binding's RHS sees the scope produced by all earlier
            // bindings; that's the crux of let*'s semantics versus let. For
            // slots after the first, ~50% of the time force an explicit
            // reference to a prior binding so the inter-binding data dependency
            // is guaranteed rather than left to GenIntLeaf's chance var-pick.
            string rhs;
            if (i > 0 && _ctx.Rng.NextDouble() < 0.5)
            {
                var prior = names[_ctx.Rng.Next(names.Count)];
                rhs = $"(+ {prior} {_exprs.GenInt(bindScope, depth - 1)})";
            }
            else
            {
                rhs = _exprs.GenInt(bindScope, depth - 1);
            }

            bindings.Add($"[{name} {rhs}]");
            bindScope = bindScope.Extend(name, ExprType.Int);
            names.Add(name);
        }

        var body = resultType switch
        {
            ExprType.Int => _exprs.GenInt(bindScope, depth - 1),
            ExprType.Bool => _exprs.GenBool(bindScope, depth - 1),
            _ => throw new InvalidOperationException($"Unsupported let* result: {resultType}"),
        };
        return $"(let* ({string.Join(" ", bindings)}) {body})";
    }
}
