namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over (Hash String Int) values built from
// `(hash (pair "k" v) ...)`. The String key constraint (^k notnull) is
// satisfied via the explicit String literal keys; the Int value type is pinned
// by the value sub-expression.
public sealed class StdlibHashGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibHashGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported()
    {
        return _ctx.Imports.Contains(StdlibImport.Hash);
    }

    // (hash-count (hash ...))
    public string CountToInt(Scope scope, int depth)
    {
        return $"(hash-count {BuildHash(scope, depth, out _)})";
    }

    // (unwrap-or (hash-ref h <key>) <default>)  — Option import required.
    public string GetUnwrapOrToInt(Scope scope, int depth)
    {
        var hashExpr = BuildHash(scope, depth, out var keys);
        var lookupKey =
            _ctx.Rng.NextDouble() < 0.5
                ? keys[_ctx.Rng.Next(keys.Count)]
                : StdlibSharedHelpers.QuotedShortAsciiString(_ctx);
        var def = _exprs.GenInt(scope, depth - 1);
        return $"(unwrap-or (hash-ref {hashExpr} {lookupKey}) {def})";
    }

    // (hash-count (hash-set h k v))
    public string PutCountToInt(Scope scope, int depth)
    {
        var hashExpr = BuildHash(scope, depth, out _);
        var k = StdlibSharedHelpers.QuotedShortAsciiString(_ctx);
        var v = _exprs.GenInt(scope, depth - 1);
        return $"(hash-count (hash-set {hashExpr} {k} {v}))";
    }

    // (hash-count (hash-remove h k)) — removes a known-present key half the time
    // and an arbitrary one the other half.
    public string RemoveCountToInt(Scope scope, int depth)
    {
        var hashExpr = BuildHash(scope, depth, out var keys);
        var k =
            _ctx.Rng.NextDouble() < 0.5
                ? keys[_ctx.Rng.Next(keys.Count)]
                : StdlibSharedHelpers.QuotedShortAsciiString(_ctx);
        return $"(hash-count (hash-remove {hashExpr} {k}))";
    }

    // (treelist-length (hash-keys h))  — requires TreeList import.
    public bool CanReduceKeysOrValues()
    {
        return IsImported() && _ctx.Imports.Contains(StdlibImport.TreeList);
    }

    public string KeysCountToInt(Scope scope, int depth)
    {
        return $"(treelist-length (hash-keys {BuildHash(scope, depth, out _)}))";
    }

    public string ValuesCountToInt(Scope scope, int depth)
    {
        return $"(treelist-length (hash-values {BuildHash(scope, depth, out _)}))";
    }

    // (hash-has-key? h k) — Bool-typed reducer.
    public string ContainsPredicateToBool(Scope scope, int depth)
    {
        var hashExpr = BuildHash(scope, depth, out var keys);
        var lookupKey =
            _ctx.Rng.NextDouble() < 0.5
                ? keys[_ctx.Rng.Next(keys.Count)]
                : StdlibSharedHelpers.QuotedShortAsciiString(_ctx);
        return $"(hash-has-key? {hashExpr} {lookupKey})";
    }

    // (hash-empty? h) — Bool-typed reducer.
    public string EmptyPredicateToBool(Scope scope, int depth)
    {
        return $"(hash-empty? {BuildHash(scope, depth, out _)})";
    }

    // Always emits >=1 entry so ^k=String / ^v=Int are pinned by inference.
    private string BuildHash(Scope scope, int depth, out List<string> keys)
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

        return $"(hash {string.Join(" ", pairs)})";
    }
}
