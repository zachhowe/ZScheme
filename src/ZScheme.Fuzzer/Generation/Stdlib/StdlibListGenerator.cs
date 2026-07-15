namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over stdlib/list — a singly-linked-list union
// (`Nil` | `(Cons head tail)`) with the type variable instantiated at Int.
// Every reducer returns Int so it composes back into the rest of the program.
//
// Three shapes are produced:
//   * `(length <list>)`             — exercises a simple recursive-fold
//   * `(fold <list> <init> <fn>)`   — exercises higher-order over the union
//   * `(match <list> [Nil ...] [(Cons h _) ...])` — exercises ctor-arm patterns
//     on a recursive ADT, optionally with a *nested* Cons pattern (e.g.
//     `(Cons h (Cons h2 _))`). This is the key win: it is the only generator that
//     nests ctor patterns over an *imported* (precompiled) union, which is the
//     path where the two backends' union-metadata resolution has historically
//     diverged — no per-program user union reaches it.
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
        return $"(length {BuildListOfInt(scope, depth - 1)})";
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

        return $"(fold {xs} {init} {fn})";
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
        var (consTail, consScope, needsCatchall) = BuildConsTailPattern(
            scope.Extend(headName, ExprType.Int),
            depth
        );
        var consBody = _exprs.GenInt(consScope, depth - 1);

        var arms = new List<string>
        {
            $"[Nil {nilBody}]",
            $"[(Cons {headName} {consTail}) {consBody}]",
        };
        if (needsCatchall)
        {
            var fallback = _exprs.GenInt(scope, depth - 1);
            arms.Add($"[_ {fallback}]");
        }

        return $"(match {xs} {string.Join(" ", arms)})";
    }

    // Head/tail accessors. car / list-head are applied to a guaranteed
    // non-empty `(Cons ...)`; cdr keeps the same guarantee (it raises on Nil);
    // rest is total (Nil -> Nil) so it takes an arbitrary list. A small
    // fraction emits the throwing `(car Nil)` shape wrapped in with-handlers —
    // both backends raise the same stdlib exception, so the caught-fallback
    // value is oracle-comparable.
    public string AccessorToInt(Scope scope, int depth)
    {
        var roll = _ctx.Rng.NextDouble();
        if (roll < 0.10)
        {
            var e = _ctx.Fresh();
            var op = _ctx.Rng.NextDouble() < 0.5 ? "car" : "list-head";
            var fallback = _exprs.GenInt(scope, depth - 1);
            return $"(with-handlers ([System.Exception {e}] {fallback}) ({op} Nil))";
        }

        var head = _exprs.GenInt(scope, depth - 1);
        var tail = BuildListOfInt(scope, depth - 1);
        var nonEmpty = $"(Cons {head} {tail})";
        if (roll < 0.40)
            return $"(car {nonEmpty})";
        if (roll < 0.60)
            return $"(list-head {nonEmpty})";
        if (roll < 0.80)
            return $"(length (cdr {nonEmpty}))";
        return $"(length (rest {BuildListOfInt(scope, depth - 1)}))";
    }

    // reverse / append / concat, observed through length.
    public string RearrangeLengthToInt(Scope scope, int depth)
    {
        var xs = BuildListOfInt(scope, depth - 1);
        var roll = _ctx.Rng.NextDouble();
        if (roll < 0.4)
            return $"(length (reverse {xs}))";
        if (roll < 0.7)
            return $"(length (append {xs} {_exprs.GenInt(scope, depth - 1)}))";
        return $"(length (concat {xs} {BuildListOfInt(scope, depth - 1)}))";
    }

    // (list-ref xs n) over a counted, guaranteed non-empty list with an
    // in-bounds index.
    public string NthToInt(Scope scope, int depth)
    {
        var xs = BuildCountedListOfInt(scope, depth - 1, out var count);
        var idx = _ctx.Rng.Next(count);
        return $"(list-ref {xs} {idx})";
    }

    // map / filter observed through length, or map composed into fold.
    public string MapFilterToInt(Scope scope, int depth)
    {
        var xs = BuildListOfInt(scope, depth - 1);
        var p = _ctx.Fresh();
        var lamScope = scope.Extend(p, ExprType.Int);
        var roll = _ctx.Rng.NextDouble();
        if (roll < 0.35)
        {
            var body = _exprs.GenInt(lamScope, depth - 1);
            return $"(length (map {xs} (lambda ([{p} : Int]) {body})))";
        }

        if (roll < 0.70)
        {
            var body = _exprs.GenBool(lamScope, depth - 1);
            return $"(length (filter {xs} (lambda ([{p} : Int]) {body})))";
        }

        var mapBody = _exprs.GenInt(lamScope, depth - 1);
        var init = _exprs.GenInt(scope, depth - 1);
        var acc = _ctx.Fresh();
        var x = _ctx.Fresh();
        var foldScope = scope.Extend(acc, ExprType.Int).Extend(x, ExprType.Int);
        var foldBody = _exprs.GenInt(foldScope, depth - 1);
        return $"(fold (map {xs} (lambda ([{p} : Int]) {mapBody})) {init} (lambda ([{acc} : Int] [{x} : Int]) {foldBody}))";
    }

    // (length (list e1 e2 ...)) — the variadic constructor (>=1 element so ^a
    // pins to Int; it is backed by a mutable-vector loop, a distinct path from
    // hand-rolled Cons chains).
    public string VariadicCtorLengthToInt(Scope scope, int depth)
    {
        var n = 1 + _ctx.Rng.Next(3);
        var elems = new List<string>(n);
        for (var i = 0; i < n; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(length (list {string.Join(" ", elems)}))";
    }

    // Cross-representation conversions. Caller gates on the partner module's
    // import; each shape converts and observes via the target rep's length.
    public bool CanConvertVector()
    {
        return IsImported() && _ctx.Imports.Contains(StdlibImport.Vector);
    }

    public bool CanConvertTreeList()
    {
        return IsImported() && _ctx.Imports.Contains(StdlibImport.TreeList);
    }

    public string VectorConversionToInt(Scope scope, int depth)
    {
        if (_ctx.Rng.NextDouble() < 0.5)
            return $"(vector-length (list->vector {BuildListOfInt(scope, depth - 1)}))";
        var n = 1 + _ctx.Rng.Next(4);
        var elems = new List<string>(n);
        for (var i = 0; i < n; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(length (vector->list (vector {string.Join(" ", elems)})))";
    }

    public string TreeListConversionToInt(Scope scope, int depth)
    {
        if (_ctx.Rng.NextDouble() < 0.5)
            return $"(treelist-length (list->treelist {BuildListOfInt(scope, depth - 1)}))";
        var n = 1 + _ctx.Rng.Next(4);
        var elems = new List<string>(n);
        for (var i = 0; i < n; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(length (treelist->list (treelist {string.Join(" ", elems)})))";
    }

    // Builds `Nil` or `(Cons <int> <list>)`. Depth caps the recursion so
    // generated programs stay small. Probability of growing the list shrinks
    // as depth decreases.
    private string BuildListOfInt(Scope scope, int depth)
    {
        if (depth <= 0)
            return "Nil";
        var grow = _ctx.Rng.NextDouble() < 0.6;
        if (!grow)
            return "Nil";
        var head = _exprs.GenInt(scope, depth - 1);
        var tail = BuildListOfInt(scope, depth - 1);
        return $"(Cons {head} {tail})";
    }

    // Like BuildListOfInt but guaranteed non-empty and with a known element
    // count, for reducers that need an in-bounds index.
    private string BuildCountedListOfInt(Scope scope, int depth, out int count)
    {
        count = 1 + _ctx.Rng.Next(4);
        var expr = "Nil";
        for (var i = 0; i < count; i++)
            expr = $"(Cons {_exprs.GenInt(scope, depth - 1)} {expr})";
        return expr;
    }

    // Pattern for Cons's tail field. Returns (pattern, extended scope,
    // needsCatchall). Wildcard / nested Cons are the "interesting" shapes;
    // a binder on the tail would have type `(List Int)`, which the rest of
    // the generator can't form sub-expressions over, so binders are skipped.
    private (string Pattern, Scope Scope, bool NeedsCatchall) BuildConsTailPattern(
        Scope scope,
        int depth
    )
    {
        var roll = _ctx.Rng.NextDouble();
        if (roll < 0.55 || depth <= 1)
            return ("_", scope, false);
        if (roll < 0.80)
            // Nil tail pattern. The two arms `[Nil _]` + `[(Cons h Nil) _]`
            // don't cover `(Cons h (Cons ...))`, so a catchall is required.
            return ("Nil", scope, true);
        // Nested Cons. Inner head is wildcarded (binding it would just
        // accumulate unused names) and inner tail is a wildcard — or, when
        // depth allows, one more Cons level (3-deep decision tree).
        if (depth > 2 && _ctx.Rng.NextDouble() < 0.4)
            return ("(Cons _ (Cons _ _))", scope, true);
        return ("(Cons _ _)", scope, true);
    }
}
