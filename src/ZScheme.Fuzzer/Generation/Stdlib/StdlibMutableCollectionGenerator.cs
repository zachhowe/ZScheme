namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over the three `stdlib/mutable/*` modules:
// vector, treelist, hash. Vector and hash are built from their immutable
// counterparts via `vector->mutable-vector` / `hash-copy` (so those shapes
// require the corresponding immutable module too); the mutable treelist is
// built directly with the `(mutable-treelist ...)` variadic constructor.
public sealed class StdlibMutableCollectionGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibMutableCollectionGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool VectorImported()
    {
        return _ctx.Imports.Contains(StdlibImport.MutableVector)
               && _ctx.Imports.Contains(StdlibImport.Vector);
    }

    public bool TreeListImported()
    {
        return _ctx.Imports.Contains(StdlibImport.MutableTreeList)
               && _ctx.Imports.Contains(StdlibImport.TreeList);
    }

    public bool HashImported()
    {
        return _ctx.Imports.Contains(StdlibImport.MutableHash)
               && _ctx.Imports.Contains(StdlibImport.Hash);
    }

    // (vector-length (vector->mutable-vector (vector E1 E2 ...)))
    public string VectorCountToInt(Scope scope, int depth)
    {
        return $"(vector-length {BuildMutableVector(scope, depth, out _)})";
    }

    // (let [xs ...] (begin (vector-set! xs i v) (vector-ref xs i)))
    public string VectorSetNthToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var arr = BuildMutableVector(scope, depth, out var count);
        var idx = _ctx.Rng.Next(count);
        var newVal = _exprs.GenInt(scope, depth - 1);
        return $"(let [{v} {arr}] (begin (vector-set! {v} {idx} {newVal}) (vector-ref {v} {idx})))";
    }

    // (mutable-treelist-length (let [xs ...] (begin (add! xs E1) (add! xs E2) xs)))
    public string TreeListAddCountToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var lst = BuildMutableTreeList(scope, depth);
        var n = 1 + _ctx.Rng.Next(3);
        var adds = new List<string>(n);
        for (var i = 0; i < n; i++)
            adds.Add($"(mutable-treelist-add! {v} {_exprs.GenInt(scope, depth - 1)})");
        return $"(let [{v} {lst}] (begin {string.Join(" ", adds)} (mutable-treelist-length {v})))";
    }

    // (mutable-treelist-ref xs i)
    public string TreeListNthToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var lst = BuildMutableTreeList(scope, depth, out var count);
        var idx = _ctx.Rng.Next(count);
        return $"(let [{v} {lst}] (mutable-treelist-ref {v} {idx}))";
    }

    // (hash-count (let [m ...] (begin (hash-set! m "k" v) m)))
    public string HashPutCountToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var mp = BuildMutableHash(scope, depth);
        var n = 1 + _ctx.Rng.Next(3);
        var puts = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            var key = $"\"k{i}\"";
            puts.Add($"(hash-set! {v} {key} {_exprs.GenInt(scope, depth - 1)})");
        }

        return $"(let [{v} {mp}] (begin {string.Join(" ", puts)} (hash-count {v})))";
    }

    // Always emits >=1 element so ^a is pinned to Int by the immutable vector literal.
    private string BuildMutableVector(Scope scope, int depth, out int count)
    {
        count = 1 + _ctx.Rng.Next(4); // 1..4
        var elems = new List<string>(count);
        for (var i = 0; i < count; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(vector->mutable-vector (vector {string.Join(" ", elems)}))";
    }

    private string BuildMutableTreeList(Scope scope, int depth)
    {
        return BuildMutableTreeList(scope, depth, out _);
    }

    private string BuildMutableTreeList(Scope scope, int depth, out int count)
    {
        count = 1 + _ctx.Rng.Next(4);
        var elems = new List<string>(count);
        for (var i = 0; i < count; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        return $"(mutable-treelist {string.Join(" ", elems)})";
    }

    // String-keyed Int-valued hash. Keys are unique literals so hash doesn't collapse entries.
    private string BuildMutableHash(Scope scope, int depth)
    {
        var n = 1 + _ctx.Rng.Next(3);
        var pairs = new List<string>(n);
        for (var i = 0; i < n; i++)
            pairs.Add($"(pair \"seed{i}\" {_exprs.GenInt(scope, depth - 1)})");
        return $"(hash-copy (hash {string.Join(" ", pairs)}))";
    }
}
