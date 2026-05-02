namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over the three `stdlib/mutable/*` modules:
// array, list, map. Constructors come from the immutable counterparts via
// `array->mutable-array` / `list->mutable-list` / `map->mutable-map`, so each
// shape requires the corresponding immutable stdlib module to also be imported.
public sealed class StdlibMutableCollectionGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibMutableCollectionGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool ArrayImported() =>
        _ctx.Imports.Contains(StdlibImport.MutableArray)
        && _ctx.Imports.Contains(StdlibImport.Array);

    public bool ListImported() =>
        _ctx.Imports.Contains(StdlibImport.MutableList)
        && _ctx.Imports.Contains(StdlibImport.List);

    public bool MapImported() =>
        _ctx.Imports.Contains(StdlibImport.MutableMap)
        && _ctx.Imports.Contains(StdlibImport.Map);

    // (mutable-array/count (array->mutable-array (array E1 E2 ...)))
    public string ArrayCountToInt(Scope scope, int depth) =>
        $"(mutable-array/count {BuildMutableArray(scope, depth, out _)})";

    // (let [xs ...] (begin (set! xs i v) (nth xs i)))
    public string ArraySetNthToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var arr = BuildMutableArray(scope, depth, out var count);
        var idx = _ctx.Rng.Next(count);
        var newVal = _exprs.GenInt(scope, depth - 1);
        return $"(let [{v} {arr}] (begin (mutable-array/set! {v} {idx} {newVal}) (mutable-array/nth {v} {idx})))";
    }

    // (mutable-list/count (let [xs ...] (begin (add! xs E1) (add! xs E2) xs)))
    public string ListAddCountToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var lst = BuildMutableList(scope, depth);
        var n = 1 + _ctx.Rng.Next(3);
        var adds = new List<string>(n);
        for (var i = 0; i < n; i++)
            adds.Add($"(mutable-list/add! {v} {_exprs.GenInt(scope, depth - 1)})");
        return $"(let [{v} {lst}] (begin {string.Join(" ", adds)} (mutable-list/count {v})))";
    }

    // (mutable-list/nth xs i)
    public string ListNthToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var lst = BuildMutableList(scope, depth, out var count);
        var idx = _ctx.Rng.Next(count);
        return $"(let [{v} {lst}] (mutable-list/nth {v} {idx}))";
    }

    // (mutable-map/count (let [m ...] (begin (put! m "k" v) m)))
    public string MapPutCountToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var mp = BuildMutableMap(scope, depth);
        var n = 1 + _ctx.Rng.Next(3);
        var puts = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            var key = $"\"k{i}\"";
            puts.Add($"(mutable-map/put! {v} {key} {_exprs.GenInt(scope, depth - 1)})");
        }
        return $"(let [{v} {mp}] (begin {string.Join(" ", puts)} (mutable-map/count {v})))";
    }

    // Always emits >=1 element so ^a is pinned to Int by the immutable array literal.
    private string BuildMutableArray(Scope scope, int depth, out int count)
    {
        count = 1 + _ctx.Rng.Next(4); // 1..4
        var elems = new List<string>(count);
        for (var i = 0; i < count; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(array->mutable-array (array {string.Join(" ", elems)}))";
    }

    private string BuildMutableList(Scope scope, int depth) => BuildMutableList(scope, depth, out _);

    private string BuildMutableList(Scope scope, int depth, out int count)
    {
        count = 1 + _ctx.Rng.Next(4);
        var elems = new List<string>(count);
        for (var i = 0; i < count; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(list->mutable-list (list {string.Join(" ", elems)}))";
    }

    // String-keyed Int-valued map. Keys are unique literals so map-of doesn't collapse entries.
    private string BuildMutableMap(Scope scope, int depth)
    {
        var n = 1 + _ctx.Rng.Next(3);
        var pairs = new List<string>(n);
        for (var i = 0; i < n; i++)
            pairs.Add($"(pair \"seed{i}\" {_exprs.GenInt(scope, depth - 1)})");
        return $"(map->mutable-map (map-of {string.Join(" ", pairs)}))";
    }
}
