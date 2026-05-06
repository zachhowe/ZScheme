namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over (Array Int) values built from `(array e1 e2 ...)`.
// Element type is pinned to Int by always emitting at least one element when
// needed for inference; nth/set indices are forced into bounds.
public sealed class StdlibArrayGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibArrayGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported() => _ctx.Imports.Contains(StdlibImport.Array);

    // (array/count (array e1 e2 ...))
    public string CountToInt(Scope scope, int depth) =>
        $"(array/count {BuildIntArray(scope, depth, out _)})";

    // (array/fold (array e1 e2 ...) <init> (lambda ([acc : Int] [x : Int]) body))
    public string FoldToInt(Scope scope, int depth)
    {
        var arrExpr = BuildIntArray(scope, depth, out _);
        var init = _exprs.GenInt(scope, depth - 1);
        var acc = _ctx.Fresh();
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(acc, ExprType.Int).Extend(x, ExprType.Int);
        var lamBody = _exprs.GenInt(lamScope, depth - 1);
        return $"(array/fold {arrExpr} {init} (lambda ([{acc} : Int] [{x} : Int]) {lamBody}))";
    }

    // (array/fold (array/map xs <f>) <init> <g>) — composed shape.
    public string MapFoldToInt(Scope scope, int depth)
    {
        var arrExpr = BuildIntArray(scope, depth, out _);

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

        return $"(array/fold (array/map {arrExpr} {mapLam}) {init} {foldLam})";
    }

    // (array/nth (array ...) <safe-index>)
    public string NthToInt(Scope scope, int depth)
    {
        var arrExpr = BuildIntArray(scope, depth, out var count);
        var idx = _ctx.Rng.Next(count);
        return $"(array/nth {arrExpr} {idx})";
    }

    // (array/count (array/append xs x))
    public string AppendCountToInt(Scope scope, int depth)
    {
        var arrExpr = BuildIntArray(scope, depth, out _);
        var x = _exprs.GenInt(scope, depth - 1);
        return $"(array/count (array/append {arrExpr} {x}))";
    }

    // (array/nth (array/set xs <safe-index> v) <safe-index>)
    public string SetNthToInt(Scope scope, int depth)
    {
        var arrExpr = BuildIntArray(scope, depth, out var count);
        var idx = _ctx.Rng.Next(count);
        var v = _exprs.GenInt(scope, depth - 1);
        return $"(array/nth (array/set {arrExpr} {idx} {v}) {idx})";
    }

    // (array/count (array/map xs <f>)) — standalone array/map (vs the composed
    // map+fold variant above).
    public string MapCountToInt(Scope scope, int depth)
    {
        var arrExpr = BuildIntArray(scope, depth, out _);
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenInt(lamScope, depth - 1);
        return $"(array/count (array/map {arrExpr} (lambda ([{x} : Int]) {body})))";
    }

    // (array/count (array/filter xs <pred>))
    public string FilterCountToInt(Scope scope, int depth)
    {
        var arrExpr = BuildIntArray(scope, depth, out _);
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenBool(lamScope, depth - 1);
        return $"(array/count (array/filter {arrExpr} (lambda ([{x} : Int]) {body})))";
    }

    // (array/empty? (array ...)) — Bool-typed reducer.
    public string EmptyPredicateToBool(Scope scope, int depth) =>
        $"(array/empty? {BuildIntArray(scope, depth, out _)})";

    // Always emits >=1 element so ^a is pinned to Int by inference.
    private string BuildIntArray(Scope scope, int depth, out int count)
    {
        count = 1 + _ctx.Rng.Next(5); // 1..5
        var elems = new List<string>(count);
        for (var i = 0; i < count; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(array {string.Join(" ", elems)})";
    }
}
