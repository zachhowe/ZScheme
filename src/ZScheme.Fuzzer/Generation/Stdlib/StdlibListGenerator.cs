namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over stdlib/list — a singly-linked-list union
// (`Nil` | `(Cons head tail)`) with the type variable instantiated at Int.
// Every reducer returns Int so it composes back into the rest of the program.
//
// Three shapes are produced:
//   * `(list/length <list>)`             — exercises a simple recursive-fold
//   * `(list/fold <list> <init> <fn>)`   — exercises higher-order over the union
//   * `(match <list> [Nil ...] [(Cons h _) ...])` — exercises ctor-arm patterns
//     on a recursive ADT, optionally with a *nested* Cons pattern (e.g.
//     `(Cons h (Cons h2 _))`) which is the key win for PatternCompiler
//     coverage that no current per-program user union exercises.
public sealed class StdlibListGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibListGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported()
    {
        return _ctx.Imports.Contains(StdlibImport.List);
    }

    public string LengthToInt(Scope scope, int depth)
    {
        return $"(list/length {BuildListOfInt(scope, depth - 1)})";
    }

    public string FoldToInt(Scope scope, int depth)
    {
        var xs = BuildListOfInt(scope, depth - 1);
        var init = _exprs.GenInt(scope, depth - 1);

        var accName = _ctx.Fresh();
        var xName = _ctx.Fresh();
        var bodyScope = scope.Extend(accName, ExprType.Int).Extend(xName, ExprType.Int);
        var bodyExpr = _exprs.GenInt(bodyScope, depth - 1);
        var fn = $"(lambda ([{accName} : Int] [{xName} : Int]) {bodyExpr})";

        return $"(list/fold {xs} {init} {fn})";
    }

    public string MatchToInt(Scope scope, int depth)
    {
        var xs = BuildListOfInt(scope, depth - 1);

        // Two arm shapes:
        //   nilBody for `[Nil ...]` — no binders.
        //   consPattern + consBody — bind head, tail varies (binder / wildcard /
        //     nested Cons pattern, with nested forcing a catchall arm).
        var nilBody = _exprs.GenInt(scope, depth - 1);

        var headName = _ctx.Fresh();
        var (consTail, consScope, needsCatchall) = BuildConsTailPattern(scope.Extend(headName, ExprType.Int), depth);
        var consBody = _exprs.GenInt(consScope, depth - 1);

        var arms = new List<string>
        {
            $"[Nil {nilBody}]",
            $"[(Cons {headName} {consTail}) {consBody}]"
        };
        if (needsCatchall)
        {
            var fallback = _exprs.GenInt(scope, depth - 1);
            arms.Add($"[_ {fallback}]");
        }

        return $"(match {xs} {string.Join(" ", arms)})";
    }

    // Builds `Nil` or `(Cons <int> <list>)`. Depth caps the recursion so
    // generated programs stay small. Probability of growing the list shrinks
    // as depth decreases.
    private string BuildListOfInt(Scope scope, int depth)
    {
        if (depth <= 0) return "Nil";
        var grow = _ctx.Rng.NextDouble() < 0.6;
        if (!grow) return "Nil";
        var head = _exprs.GenInt(scope, depth - 1);
        var tail = BuildListOfInt(scope, depth - 1);
        return $"(Cons {head} {tail})";
    }

    // Pattern for Cons's tail field. Returns (pattern, extended scope,
    // needsCatchall). Wildcard / nested Cons are the "interesting" shapes;
    // a binder on the tail would have type `(List Int)`, which the rest of
    // the generator can't form sub-expressions over, so binders are skipped.
    private (string Pattern, Scope Scope, bool NeedsCatchall) BuildConsTailPattern(
        Scope scope, int depth)
    {
        var roll = _ctx.Rng.NextDouble();
        if (roll < 0.55 || depth <= 1) return ("_", scope, false);
        if (roll < 0.80)
            // Nil tail pattern. The two arms `[Nil _]` + `[(Cons h Nil) _]`
            // don't cover `(Cons h (Cons ...))`, so a catchall is required.
            return ("Nil", scope, true);
        // Nested Cons. Inner head is wildcarded (binding it would just
        // accumulate unused names) and inner tail is a wildcard.
        return ("(Cons _ _)", scope, true);
    }
}
