namespace ZScheme.Fuzzer.Generation;

// Single source of truth for `:where (...)` suffixes. Used by UserFuncGenerator
// (generic functions), UserTypeGenerator (generic records/unions), and
// GenericClassGenerator.
//
// The fuzzer instantiates type params at value-type primitives (Int / Bool /
// Float) which all round-trip back to Int, so the constraint kinds emitted here
// are those compatible with that grounding: `struct`, `unmanaged`, `default`,
// `notnull` (value types are non-nullable), and `new` (value types have
// parameterless ctors) — all verified end-to-end on both backends. `class` is
// excluded: no reference-type ground exists to instantiate it with.
public sealed class WhereConstraintGenerator
{
    private static readonly string[] SafeConstraints =
    [
        "struct",
        "unmanaged",
        "default",
        "notnull",
        "new",
    ];

    private readonly GeneratorContext _ctx;

    public WhereConstraintGenerator(GeneratorContext ctx)
    {
        _ctx = ctx;
    }

    // Returns either an empty string or " :where (...)" with a randomly chosen
    // subset of typeParams constrained. Probability of any constraint at all
    // is `emitProbability`; per-param constraint probability inside that is 0.5.
    public string MaybeEmit(IReadOnlyList<string> typeParams, double emitProbability = 0.15)
    {
        if (typeParams.Count == 0)
            return "";
        if (_ctx.Rng.NextDouble() >= emitProbability)
            return "";

        // Per-param constraint probability bumped to 0.7 so multi-param
        // signatures (e.g. ^a/^b) frequently emit constraints on both — the
        // multi-clause `:where ((^a struct) (^b unmanaged))` form is otherwise
        // rare. Single-param signatures are unaffected at the upper bound.
        var picked = new List<(string Tp, string C)>();
        foreach (var tp in typeParams)
            if (_ctx.Rng.NextDouble() < 0.7)
                picked.Add((tp, SafeConstraints[_ctx.Rng.Next(SafeConstraints.Length)]));
        if (picked.Count == 0)
        {
            // We rolled to emit — force at least one.
            var tp = typeParams[_ctx.Rng.Next(typeParams.Count)];
            picked.Add((tp, SafeConstraints[_ctx.Rng.Next(SafeConstraints.Length)]));
        }

        // ZScheme accepts both `:where (^a struct)` (single) and
        // `:where ((^a struct) (^b unmanaged))` (multiple).
        if (picked.Count == 1)
            return $" :where ({picked[0].Tp} {picked[0].C})";
        var clauses = picked.Select(p => $"({p.Tp} {p.C})");
        return $" :where ({string.Join(" ", clauses)})";
    }
}
