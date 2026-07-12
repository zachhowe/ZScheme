namespace ZScheme.Fuzzer.Generation;

// Emits Symbol-typed expressions ('lit literals, string->symbol / symbol->string
// round-trips, symbol match arms) reduced to Int. Symbols need no import — the
// quote form and both conversions are builtins backed by ZScheme.Runtime.ZSymbol.
//
// The two backends lower symbol equality differently (the C# emitter compares
// interned ZSymbol instances with ==, the IL emitter emits an Intern call plus
// ceq), and symbol *literal* match arms take a third path per backend (C#
// `when` guards vs the IL pattern-test sequence), so equality across the two
// construction paths — quoted literal vs `(string->symbol ...)` — is the core
// probe: it is exactly the shape where interning must make reference equality
// behave like value equality.
//
// Symbols are kept local to each emitted expression (no ExprType.Symbol / no
// Scope entries): a scope-flowing symbol var would be unusable by the rest of
// the generator, which only forms sub-expressions over Int/Bool/Float/String.
public sealed class SymbolExprGenerator
{
    // Valid ZScheme atoms only; hyphenated names exercise the lexer's
    // symbol-continue set inside a quote form.
    private static readonly string[] NamePool = ["a", "b", "foo", "x1", "my-sym", "zz"];

    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public SymbolExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    private string PickName()
    {
        return NamePool[_ctx.Rng.Next(NamePool.Length)];
    }

    // A Symbol-typed sub-expression over a known name: the quoted literal, the
    // string->symbol construction, or the full round-trip — three distinct
    // construction paths that must all intern to the same instance.
    private string SymbolExprFor(string name)
    {
        return _ctx.Rng.Next(3) switch
        {
            0 => $"'{name}",
            1 => $"(string->symbol \"{name}\")",
            _ => $"(string->symbol (symbol->string '{name}))",
        };
    }

    // (if (= <sym-a> <sym-b>) t e) — equal or distinct names, mixed
    // construction paths, `=` or `!=`.
    public string SymbolEqToInt(Scope scope, int depth)
    {
        var a = PickName();
        var b = _ctx.Rng.NextDouble() < 0.5 ? a : PickName();
        var op = _ctx.Rng.NextDouble() < 0.5 ? "=" : "!=";
        var t = _exprs.GenInt(scope, depth - 1);
        var e = _exprs.GenInt(scope, depth - 1);
        return $"(if ({op} {SymbolExprFor(a)} {SymbolExprFor(b)}) {t} {e})";
    }

    // (if (= (symbol->string 'name) "name") t e) — the String side of the
    // round-trip. Equal contents with a computed left side is the shape that
    // caught the IL backend comparing strings by reference (bare ceq); it is
    // split evenly with distinct names so both the hit and miss paths stay
    // covered.
    public string SymbolToStringEqToInt(Scope scope, int depth)
    {
        var name = PickName();
        var other = _ctx.Rng.NextDouble() < 0.5 ? name : PickDifferentName(name);
        var t = _exprs.GenInt(scope, depth - 1);
        var e = _exprs.GenInt(scope, depth - 1);
        return $"(if (= (symbol->string '{name}) \"{other}\") {t} {e})";
    }

    private string PickDifferentName(string name)
    {
        string other;
        do
        {
            other = PickName();
        } while (other == name);
        return other;
    }

    // (match <sym> ['a body] ['b body] [_ fallback]) — symbol literal patterns.
    // The scrutinee mixes construction paths; under the fall-through probe the
    // catchall may be omitted (symbol-literal matches are Warning-only).
    public string SymbolMatchToInt(Scope scope, int depth)
    {
        var scrutName = PickName();
        var scrut = SymbolExprFor(scrutName);

        var numArms = 1 + _ctx.Rng.Next(3);
        var used = new HashSet<string>();
        var arms = new List<string>();
        // Bias one arm toward the scrutinee's own name so hits are common.
        if (_ctx.Rng.NextDouble() < 0.7)
        {
            used.Add(scrutName);
            arms.Add($"['{scrutName} {_exprs.GenInt(scope, depth - 1)}]");
        }

        for (var i = arms.Count; i < numArms; i++)
        {
            var n = PickName();
            if (!used.Add(n))
                continue;
            arms.Add($"['{n} {_exprs.GenInt(scope, depth - 1)}]");
        }

        if (_ctx.EnableMatchFallthrough && _ctx.Rng.NextDouble() < 0.4)
            return _exprs.WrapMatchFallthrough(
                $"(match {scrut} {string.Join(" ", arms)})",
                ExprType.Int,
                scope,
                depth
            );

        arms.Add($"[_ {_exprs.GenInt(scope, depth - 1)}]");
        return $"(match {scrut} {string.Join(" ", arms)})";
    }

    // (let ([s <sym>]) (if (= s <sym>) t e)) — a symbol flowing through a
    // let-bound local (kept out of Scope; the binder is only referenced here).
    public string SymbolLetToInt(Scope scope, int depth)
    {
        var name = PickName();
        var binder = _ctx.Fresh();
        var t = _exprs.GenInt(scope, depth - 1);
        var e = _exprs.GenInt(scope, depth - 1);
        return $"(let ([{binder} {SymbolExprFor(name)}]) (if (= {binder} {SymbolExprFor(name)}) {t} {e}))";
    }
}
