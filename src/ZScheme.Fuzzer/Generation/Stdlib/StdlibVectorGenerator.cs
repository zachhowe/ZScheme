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
