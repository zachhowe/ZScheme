namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over (Map String Int) values built from
// `(map-of (pair "k" v) ...)`. The String key constraint (^k notnull) is
// satisfied via the explicit String literal keys; the Int value type is pinned
// by the value sub-expression.
public sealed class StdlibMapGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibMapGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported() => _ctx.Imports.Contains(StdlibImport.Map);

    // (map/count (map-of ...))
    public string CountToInt(Scope scope, int depth) =>
        $"(map/count {BuildMap(scope, depth, out _)})";

    // (option/unwrap-or (map/get m <key>) <default>)  — Option import required.
    public string GetUnwrapOrToInt(Scope scope, int depth)
    {
        var mapExpr = BuildMap(scope, depth, out var keys);
        var lookupKey = _ctx.Rng.NextDouble() < 0.5
            ? keys[_ctx.Rng.Next(keys.Count)]
            : StdlibSharedHelpers.QuotedShortAsciiString(_ctx);
        var def = _exprs.GenInt(scope, depth - 1);
        return $"(option/unwrap-or (map/get {mapExpr} {lookupKey}) {def})";
    }

    // (map/count (map/put m k v))
    public string PutCountToInt(Scope scope, int depth)
    {
        var mapExpr = BuildMap(scope, depth, out _);
        var k = StdlibSharedHelpers.QuotedShortAsciiString(_ctx);
        var v = _exprs.GenInt(scope, depth - 1);
        return $"(map/count (map/put {mapExpr} {k} {v}))";
    }

    // (map/count (map/remove m k)) — removes a known-present key half the time
    // and an arbitrary one the other half.
    public string RemoveCountToInt(Scope scope, int depth)
    {
        var mapExpr = BuildMap(scope, depth, out var keys);
        var k = _ctx.Rng.NextDouble() < 0.5
            ? keys[_ctx.Rng.Next(keys.Count)]
            : StdlibSharedHelpers.QuotedShortAsciiString(_ctx);
        return $"(map/count (map/remove {mapExpr} {k}))";
    }

    // (treelist/count (map/keys m))  — requires TreeList import.
    public bool CanReduceKeysOrValues() =>
        IsImported() && _ctx.Imports.Contains(StdlibImport.TreeList);

    public string KeysCountToInt(Scope scope, int depth) =>
        $"(treelist/count (map/keys {BuildMap(scope, depth, out _)}))";

    public string ValuesCountToInt(Scope scope, int depth) =>
        $"(treelist/count (map/values {BuildMap(scope, depth, out _)}))";

    // (map/contains-key? m k) — Bool-typed reducer.
    public string ContainsPredicateToBool(Scope scope, int depth)
    {
        var mapExpr = BuildMap(scope, depth, out var keys);
        var lookupKey = _ctx.Rng.NextDouble() < 0.5
            ? keys[_ctx.Rng.Next(keys.Count)]
            : StdlibSharedHelpers.QuotedShortAsciiString(_ctx);
        return $"(map/contains-key? {mapExpr} {lookupKey})";
    }

    // (map/empty? m) — Bool-typed reducer.
    public string EmptyPredicateToBool(Scope scope, int depth) =>
        $"(map/empty? {BuildMap(scope, depth, out _)})";

    // Always emits >=1 entry so ^k=String / ^v=Int are pinned by inference.
    private string BuildMap(Scope scope, int depth, out List<string> keys)
    {
        var n = 1 + _ctx.Rng.Next(4); // 1..4
        var pairs = new List<string>(n);
        keys = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            var key = StdlibSharedHelpers.QuotedShortAsciiString(_ctx);
            keys.Add(key);
            var value = _exprs.GenInt(scope, depth - 1);
            pairs.Add($"(pair {key} {value})");
        }
        return $"(map-of {string.Join(" ", pairs)})";
    }
}
