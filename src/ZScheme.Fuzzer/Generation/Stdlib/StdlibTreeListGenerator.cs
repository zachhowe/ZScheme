namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over (TreeList Int) values built from `(treelist e1 e2 ...)`
// where every ei is an Int sub-expression. Element type is pinned to Int by
// always emitting at least one element when needed for inference. All Int
// reducers terminate via treelist/count, treelist/fold, or a deterministic safe index.
public sealed class StdlibTreeListGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibTreeListGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported() => _ctx.Imports.Contains(StdlibImport.TreeList);

    // (treelist/count (treelist ...))
    public string CountToInt(Scope scope, int depth) =>
        $"(treelist/count {BuildIntTreeList(scope, depth, allowEmpty: true, out _)})";

    // (treelist/fold (treelist ...) <init> (lambda ([acc : Int] [x : Int]) body))
    public string FoldToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntTreeList(scope, depth, allowEmpty: true, out _);
        var init = _exprs.GenInt(scope, depth - 1);
        var acc = _ctx.Fresh();
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(acc, ExprType.Int).Extend(x, ExprType.Int);
        var lamBody = _exprs.GenInt(lamScope, depth - 1);
        return $"(treelist/fold {listExpr} {init} (lambda ([{acc} : Int] [{x} : Int]) {lamBody}))";
    }

    // (treelist/nth (treelist e0 e1 ...) <safe-index>)  — index forced into [0, count).
    public string NthToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntTreeList(scope, depth, allowEmpty: false, out var count);
        var idx = _ctx.Rng.Next(count);
        return $"(treelist/nth {listExpr} {idx})";
    }

    // (treelist/head (treelist e0 e1 ...))  — list always non-empty.
    public string HeadToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntTreeList(scope, depth, allowEmpty: false, out _);
        return $"(treelist/head {listExpr})";
    }

    // (treelist/count (treelist/tail (treelist e0 e1 ...)))  — list always non-empty.
    public string TailCountToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntTreeList(scope, depth, allowEmpty: false, out _);
        return $"(treelist/count (treelist/tail {listExpr}))";
    }

    // (treelist/count (treelist/cons x xs))
    public string ConsCountToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntTreeList(scope, depth, allowEmpty: true, out _);
        var x = _exprs.GenInt(scope, depth - 1);
        return $"(treelist/count (treelist/cons {x} {listExpr}))";
    }

    // (treelist/count (treelist/append xs x))
    public string AppendCountToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntTreeList(scope, depth, allowEmpty: true, out _);
        var x = _exprs.GenInt(scope, depth - 1);
        return $"(treelist/count (treelist/append {listExpr} {x}))";
    }

    // (treelist/count (treelist/concat xs ys))
    public string ConcatCountToInt(Scope scope, int depth)
    {
        var xs = BuildIntTreeList(scope, depth, allowEmpty: true, out _);
        var ys = BuildIntTreeList(scope, depth, allowEmpty: true, out _);
        return $"(treelist/count (treelist/concat {xs} {ys}))";
    }

    // (treelist/count (treelist/map xs (lambda ([x : Int]) body)))
    public string MapCountToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntTreeList(scope, depth, allowEmpty: true, out _);
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenInt(lamScope, depth - 1);
        return $"(treelist/count (treelist/map {listExpr} (lambda ([{x} : Int]) {body})))";
    }

    // (treelist/count (treelist/filter xs (lambda ([x : Int]) <bool>)))
    public string FilterCountToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntTreeList(scope, depth, allowEmpty: true, out _);
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenBool(lamScope, depth - 1);
        return $"(treelist/count (treelist/filter {listExpr} (lambda ([{x} : Int]) {body})))";
    }

    // (treelist/empty? (treelist ...)) — Bool-typed reducer.
    public string EmptyPredicateToBool(Scope scope, int depth) =>
        $"(treelist/empty? {BuildIntTreeList(scope, depth, allowEmpty: true, out _)})";

    // Builds `(treelist e1 e2 ...)` of 0-5 (or 1-5) Int sub-expressions, always
    // forcing element-type inference to Int. Returns the source text and the
    // number of elements emitted (caller may need it for safe indexing).
    private string BuildIntTreeList(Scope scope, int depth, bool allowEmpty, out int count)
    {
        count = allowEmpty ? _ctx.Rng.Next(6) : 1 + _ctx.Rng.Next(5);
        if (count == 0) return "(treelist)";
        var elems = new List<string>(count);
        for (var i = 0; i < count; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(treelist {string.Join(" ", elems)})";
    }
}
