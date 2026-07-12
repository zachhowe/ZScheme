namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over (Vector Int) values built from `(vector e1 e2 ...)`.
// Element type is pinned to Int by always emitting at least one element when
// needed for inference; ref/set indices are forced into bounds.
public sealed class StdlibVectorGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibVectorGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported()
    {
        return _ctx.Imports.Contains(StdlibImport.Vector);
    }

    // (vector-length (vector e1 e2 ...))
    public string CountToInt(Scope scope, int depth)
    {
        return $"(vector-length {BuildIntVector(scope, depth, out _)})";
    }

    // (vector-foldl (vector e1 e2 ...) <init> (lambda ([acc : Int] [x : Int]) body))
    public string FoldToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out _);
        var init = _exprs.GenInt(scope, depth - 1);
        var acc = _ctx.Fresh();
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(acc, ExprType.Int).Extend(x, ExprType.Int);
        var lamBody = _exprs.GenInt(lamScope, depth - 1);
        return $"(vector-foldl {vecExpr} {init} (lambda ([{acc} : Int] [{x} : Int]) {lamBody}))";
    }

    // (vector-foldl (vector-map xs <f>) <init> <g>) — composed shape.
    public string MapFoldToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out _);

        var mapParam = _ctx.Fresh();
        var mapBodyScope = scope.Extend(mapParam, ExprType.Int);
        var mapBody = _exprs.GenInt(mapBodyScope, depth - 1);
        var mapLam = $"(lambda ([{mapParam} : Int]) {mapBody})";

        var init = _exprs.GenInt(scope, depth - 1);
        var acc = _ctx.Fresh();
        var x = _ctx.Fresh();
        var foldScope = scope.Extend(acc, ExprType.Int).Extend(x, ExprType.Int);
        var foldBody = _exprs.GenInt(foldScope, depth - 1);
        var foldLam = $"(lambda ([{acc} : Int] [{x} : Int]) {foldBody})";

        return $"(vector-foldl (vector-map {vecExpr} {mapLam}) {init} {foldLam})";
    }

    // (vector-ref (vector ...) <safe-index>)
    public string NthToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out var count);
        var idx = _ctx.Rng.Next(count);
        return $"(vector-ref {vecExpr} {idx})";
    }

    // (vector-length (vector-append xs (vector x))) — vector-append concatenates
    // vectors, so the appended element is wrapped in a singleton vector.
    public string AppendCountToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out _);
        var x = _exprs.GenInt(scope, depth - 1);
        return $"(vector-length (vector-append {vecExpr} (vector {x})))";
    }

    // (vector-ref (vector-set/copy xs <safe-index> v) <safe-index>)
    public string SetNthToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out var count);
        var idx = _ctx.Rng.Next(count);
        var v = _exprs.GenInt(scope, depth - 1);
        return $"(vector-ref (vector-set/copy {vecExpr} {idx} {v}) {idx})";
    }

    // (vector-length (vector-map xs <f>)) — standalone vector-map (vs the composed
    // map+fold variant above).
    public string MapCountToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out _);
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenInt(lamScope, depth - 1);
        return $"(vector-length (vector-map {vecExpr} (lambda ([{x} : Int]) {body})))";
    }

    // (vector-length (vector-filter xs <pred>))
    public string FilterCountToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out _);
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenBool(lamScope, depth - 1);
        return $"(vector-length (vector-filter {vecExpr} (lambda ([{x} : Int]) {body})))";
    }

    // (vector-empty? (vector ...)) — Bool-typed reducer.
    public string EmptyPredicateToBool(Scope scope, int depth)
    {
        return $"(vector-empty? {BuildIntVector(scope, depth, out _)})";
    }

    // (vector-ref (make-vector n v) idx) / (vector-ref (build-vector n f) idx)
    // — the two counted constructors, bundled so the GenInt table stays balanced.
    public string MakeOrBuildRefToInt(Scope scope, int depth)
    {
        var n = 1 + _ctx.Rng.Next(5);
        var idx = _ctx.Rng.Next(n);
        if (_ctx.Rng.NextDouble() < 0.5)
        {
            var v = _exprs.GenInt(scope, depth - 1);
            return $"(vector-ref (make-vector {n} {v}) {idx})";
        }

        var i = _ctx.Fresh();
        var lamScope = scope.Extend(i, ExprType.Int);
        var body = _exprs.GenInt(lamScope, depth - 1);
        return $"(vector-ref (build-vector {n} (lambda ([{i} : Int]) {body})) {idx})";
    }

    // (vector-ref (vector-sort xs less?) idx). Both backends run the same
    // stdlib sort, so any comparator (even a non-order) stays deterministic;
    // mostly use a real order so the result is value-meaningful.
    public string SortRefToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out var count);
        var idx = _ctx.Rng.Next(count);
        var a = _ctx.Fresh();
        var b = _ctx.Fresh();
        var roll = _ctx.Rng.NextDouble();
        string body;
        if (roll < 0.45)
            body = $"(< {a} {b})";
        else if (roll < 0.90)
            body = $"(> {a} {b})";
        else
            body = _exprs.GenBool(scope.Extend(a, ExprType.Int).Extend(b, ExprType.Int), depth - 1);
        return $"(vector-ref (vector-sort {vecExpr} (lambda ([{a} : Int] [{b} : Int]) {body})) {idx})";
    }

    // (vector-length (vector-take xs k)) / (vector-length (vector-drop xs k)),
    // k forced into [0, count] so the copy loops stay in bounds.
    public string TakeDropCountToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out var count);
        var k = _ctx.Rng.Next(count + 1);
        var op = _ctx.Rng.NextDouble() < 0.5 ? "vector-take" : "vector-drop";
        return $"(vector-length ({op} {vecExpr} {k}))";
    }

    // (vector-count xs pred) / (vector-length (vector-filter-not xs pred)).
    public string CountOrFilterNotToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out _);
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenBool(lamScope, depth - 1);
        var lam = $"(lambda ([{x} : Int]) {body})";
        return _ctx.Rng.NextDouble() < 0.5
            ? $"(vector-count {vecExpr} {lam})"
            : $"(vector-length (vector-filter-not {vecExpr} {lam}))";
    }

    // (vector-argmin xs f) / (vector-argmax xs f) — vector always non-empty
    // (arg-loop has no empty-vector case), key fn is Int-valued.
    public string ArgMinMaxToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out _);
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenInt(lamScope, depth - 1);
        var op = _ctx.Rng.NextDouble() < 0.5 ? "vector-argmin" : "vector-argmax";
        return $"({op} {vecExpr} (lambda ([{x} : Int]) {body}))";
    }

    // (vector-length (vector-append v1 v2 v3)) — the variadic form with 2-4
    // vector args (the existing AppendCountToInt only ever passes two).
    public string AppendManyCountToInt(Scope scope, int depth)
    {
        var n = 2 + _ctx.Rng.Next(3);
        var vecs = new List<string>(n);
        for (var i = 0; i < n; i++)
            vecs.Add(BuildIntVector(scope, depth, out _));
        return $"(vector-length (vector-append {string.Join(" ", vecs)}))";
    }

    // (unwrap-or (vector-member xs x) k) — vector-member returns (Option Int);
    // caller must gate on the Option import.
    public string MemberUnwrapOrToInt(Scope scope, int depth)
    {
        var vecExpr = BuildIntVector(scope, depth, out _);
        var x = _exprs.GenInt(scope, depth - 1);
        var dflt = _exprs.GenInt(scope, depth - 1);
        return $"(unwrap-or (vector-member {vecExpr} {x}) {dflt})";
    }

    // (length (vector->list (vector ...))) — cross-module conversion; caller
    // must gate on the List import.
    public string ToListLengthToInt(Scope scope, int depth)
    {
        return $"(length (vector->list {BuildIntVector(scope, depth, out _)}))";
    }

    // Always emits >=1 element so ^a is pinned to Int by inference.
    private string BuildIntVector(Scope scope, int depth, out int count)
    {
        count = 1 + _ctx.Rng.Next(5); // 1..5
        var elems = new List<string>(count);
        for (var i = 0; i < count; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(vector {string.Join(" ", elems)})";
    }
}
