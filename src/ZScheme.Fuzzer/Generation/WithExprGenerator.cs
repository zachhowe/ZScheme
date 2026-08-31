namespace ZScheme.Fuzzer.Generation;

// Emits `(RecName-field (with (RecName v1 v2 ...) [fld new-val] ...))`, exercising
// the `RecordWith` IR path. Works unchanged on struct-declared records; the accessor
// and `with` syntax are identical for both.
//
// All generic user records are Int-monomorphized elsewhere in the fuzzer, so every
// field receives an Int expression. Gated on `_ctx.UserRecords.Count > 0` at the
// call site in ExprGenerator.
public sealed class WithExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public WithExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public string WithUpdateToInt(Scope scope, int depth)
    {
        var r = _ctx.UserRecords[_ctx.Rng.Next(_ctx.UserRecords.Count)];

        var initialArgs = new List<string>();
        foreach (var _ in r.Fields)
            initialArgs.Add(_exprs.GenInt(scope, depth - 1));

        var fieldIndices = Enumerable.Range(0, r.Fields.Count).ToList();
        Shuffle(fieldIndices);
        var numUpdates = 1 + _ctx.Rng.Next(r.Fields.Count);
        var updateParts = new List<string>();
        for (var i = 0; i < numUpdates; i++)
        {
            var f = r.Fields[fieldIndices[i]];
            var v = _exprs.GenInt(scope, depth - 1);
            updateParts.Add($"[{f.Name} {v}]");
        }

        var readField = r.Fields[_ctx.Rng.Next(r.Fields.Count)].Name;

        var recordExpr = $"({r.Name} {string.Join(" ", initialArgs)})";
        var withExpr = $"(with {recordExpr} {string.Join(" ", updateParts)})";
        return $"({r.Name}-{readField} {withExpr})";
    }

    private void Shuffle<T>(List<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _ctx.Rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
