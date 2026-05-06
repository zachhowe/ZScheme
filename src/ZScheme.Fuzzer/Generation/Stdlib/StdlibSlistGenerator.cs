namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over stdlib/slist — a singly-linked-list union
// (`SNil` | `(SCons head tail)`) with the type variable instantiated at Int.
// Every reducer returns Int so it composes back into the rest of the program.
//
// Three shapes are produced:
//   * `(slist/length <slist>)`             — exercises a simple recursive-fold
//   * `(slist/fold <slist> <init> <fn>)`   — exercises higher-order over the union
//   * `(match <slist> [SNil ...] [(SCons h _) ...])` — exercises ctor-arm patterns
//     on a recursive ADT, optionally with a *nested* SCons pattern (e.g.
//     `(SCons h (SCons h2 _))`) which is the key win for PatternCompiler
//     coverage that no current per-program user union exercises.
public sealed class StdlibSlistGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibSlistGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported() => _ctx.Imports.Contains(StdlibImport.Slist);

    public string LengthToInt(Scope scope, int depth) =>
        $"(slist/length {BuildSlistOfInt(scope, depth - 1)})";

    public string FoldToInt(Scope scope, int depth)
    {
        var xs = BuildSlistOfInt(scope, depth - 1);
        var init = _exprs.GenInt(scope, depth - 1);

        var accName = _ctx.Fresh();
        var xName = _ctx.Fresh();
        var bodyScope = scope.Extend(accName, ExprType.Int).Extend(xName, ExprType.Int);
        var bodyExpr = _exprs.GenInt(bodyScope, depth - 1);
        var fn = $"(lambda ([{accName} : Int] [{xName} : Int]) {bodyExpr})";

        return $"(slist/fold {xs} {init} {fn})";
    }

    public string MatchToInt(Scope scope, int depth)
    {
        var xs = BuildSlistOfInt(scope, depth - 1);

        // Two arm shapes:
        //   nilBody for `[SNil ...]` — no binders.
        //   consPattern + consBody — bind head, tail varies (binder / wildcard /
        //     nested SCons pattern, with nested forcing a catchall arm).
        var nilBody = _exprs.GenInt(scope, depth - 1);

        var headName = _ctx.Fresh();
        var (consTail, consScope, needsCatchall) = BuildConsTailPattern(scope.Extend(headName, ExprType.Int), depth);
        var consBody = _exprs.GenInt(consScope, depth - 1);

        var arms = new List<string>
        {
            $"[SNil {nilBody}]",
            $"[(SCons {headName} {consTail}) {consBody}]",
        };
        if (needsCatchall)
        {
            var fallback = _exprs.GenInt(scope, depth - 1);
            arms.Add($"[_ {fallback}]");
        }
        return $"(match {xs} {string.Join(" ", arms)})";
    }

    // Builds `SNil` or `(SCons <int> <slist>)`. Depth caps the recursion so
    // generated programs stay small. Probability of growing the list shrinks
    // as depth decreases.
    private string BuildSlistOfInt(Scope scope, int depth)
    {
        if (depth <= 0) return "SNil";
        var grow = _ctx.Rng.NextDouble() < 0.6;
        if (!grow) return "SNil";
        var head = _exprs.GenInt(scope, depth - 1);
        var tail = BuildSlistOfInt(scope, depth - 1);
        return $"(SCons {head} {tail})";
    }

    // Pattern for SCons's tail field. Returns (pattern, extended scope,
    // needsCatchall). Wildcard / nested SCons are the "interesting" shapes;
    // a binder on the tail would have type `(SList Int)`, which the rest of
    // the generator can't form sub-expressions over, so binders are skipped.
    private (string Pattern, Scope Scope, bool NeedsCatchall) BuildConsTailPattern(
        Scope scope, int depth)
    {
        var roll = _ctx.Rng.NextDouble();
        if (roll < 0.55 || depth <= 1)
        {
            return ("_", scope, false);
        }
        if (roll < 0.80)
        {
            // SNil tail pattern. The two arms `[SNil _]` + `[(SCons h SNil) _]`
            // don't cover `(SCons h (SCons ...))`, so a catchall is required.
            return ("SNil", scope, true);
        }
        // Nested SCons. Inner head is wildcarded (binding it would just
        // accumulate unused names) and inner tail is a wildcard.
        return ("(SCons _ _)", scope, true);
    }
}
