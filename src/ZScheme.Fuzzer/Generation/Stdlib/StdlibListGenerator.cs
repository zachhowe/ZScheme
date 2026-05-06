namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over (List Int) values built from `(list e1 e2 ...)`
// where every ei is an Int sub-expression. Element type is pinned to Int by
// always emitting at least one element when needed for inference. All Int
// reducers terminate via list/count, list/fold, or a deterministic safe index.
public sealed class StdlibListGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibListGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported() => _ctx.Imports.Contains(StdlibImport.List);

    // (list/count (list ...))
    public string CountToInt(Scope scope, int depth) =>
        $"(list/count {BuildIntList(scope, depth, allowEmpty: true, out _)})";

    // (list/fold (list ...) <init> (lambda ([acc : Int] [x : Int]) body))
    public string FoldToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntList(scope, depth, allowEmpty: true, out _);
        var init = _exprs.GenInt(scope, depth - 1);
        var acc = _ctx.Fresh();
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(acc, ExprType.Int).Extend(x, ExprType.Int);
        var lamBody = _exprs.GenInt(lamScope, depth - 1);
        return $"(list/fold {listExpr} {init} (lambda ([{acc} : Int] [{x} : Int]) {lamBody}))";
    }

    // (list/nth (list e0 e1 ...) <safe-index>)  — index forced into [0, count).
    public string NthToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntList(scope, depth, allowEmpty: false, out var count);
        var idx = _ctx.Rng.Next(count);
        return $"(list/nth {listExpr} {idx})";
    }

    // (list/head (list e0 e1 ...))  — list always non-empty.
    public string HeadToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntList(scope, depth, allowEmpty: false, out _);
        return $"(list/head {listExpr})";
    }

    // (list/count (list/tail (list e0 e1 ...)))  — list always non-empty.
    public string TailCountToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntList(scope, depth, allowEmpty: false, out _);
        return $"(list/count (list/tail {listExpr}))";
    }

    // (list/count (list/cons x xs))
    public string ConsCountToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntList(scope, depth, allowEmpty: true, out _);
        var x = _exprs.GenInt(scope, depth - 1);
        return $"(list/count (list/cons {x} {listExpr}))";
    }

    // (list/count (list/append xs x))
    public string AppendCountToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntList(scope, depth, allowEmpty: true, out _);
        var x = _exprs.GenInt(scope, depth - 1);
        return $"(list/count (list/append {listExpr} {x}))";
    }

    // (list/count (list/concat xs ys))
    public string ConcatCountToInt(Scope scope, int depth)
    {
        var xs = BuildIntList(scope, depth, allowEmpty: true, out _);
        var ys = BuildIntList(scope, depth, allowEmpty: true, out _);
        return $"(list/count (list/concat {xs} {ys}))";
    }

    // (list/count (list/map xs (lambda ([x : Int]) body)))
    public string MapCountToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntList(scope, depth, allowEmpty: true, out _);
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenInt(lamScope, depth - 1);
        return $"(list/count (list/map {listExpr} (lambda ([{x} : Int]) {body})))";
    }

    // (list/count (list/filter xs (lambda ([x : Int]) <bool>)))
    public string FilterCountToInt(Scope scope, int depth)
    {
        var listExpr = BuildIntList(scope, depth, allowEmpty: true, out _);
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenBool(lamScope, depth - 1);
        return $"(list/count (list/filter {listExpr} (lambda ([{x} : Int]) {body})))";
    }

    // (list/empty? (list ...)) — Bool-typed reducer.
    public string EmptyPredicateToBool(Scope scope, int depth) =>
        $"(list/empty? {BuildIntList(scope, depth, allowEmpty: true, out _)})";

    // Builds `(list e1 e2 ...)` of 0-5 (or 1-5) Int sub-expressions, always
    // forcing element-type inference to Int. Returns the source text and the
    // number of elements emitted (caller may need it for safe indexing).
    private string BuildIntList(Scope scope, int depth, bool allowEmpty, out int count)
    {
        count = allowEmpty ? _ctx.Rng.Next(6) : 1 + _ctx.Rng.Next(5);
        if (count == 0) return "(list)";
        var elems = new List<string>(count);
        for (var i = 0; i < count; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(list {string.Join(" ", elems)})";
    }
}
