namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over stdlib/core exports: id and compose. Both are
// generic; the fuzzer instantiates every type variable at Int so the result
// is directly usable as an Int sub-expression.
//
// `is-null?` is intentionally not exercised here. It boils down to
// (System.Object/ReferenceEquals x null) which is well-defined for reference
// types (String) and CLR-boxed value types but the boxing path differs between
// the C# and IL backends; the existing milestone is focused on stdlib breadth,
// not that semantic edge case.
public sealed class StdlibCoreGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibCoreGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported() => _ctx.Imports.Contains(StdlibImport.Core);

    // (id <int>) — ^a unifies to Int from the argument.
    public string IdToInt(Scope scope, int depth) =>
        $"(id {_exprs.GenInt(scope, depth - 1)})";

    // (compose (fn [[x : Int]] body1) (fn [[y : Int]] body2) <int>)
    // ^a, ^b, ^c all instantiate at Int.
    public string ComposeToInt(Scope scope, int depth)
    {
        var fParam = _ctx.Fresh();
        var fScope = scope.Extend(fParam, ExprType.Int);
        var fBody = _exprs.GenInt(fScope, depth - 1);
        var f = $"(fn [[{fParam} : Int]] {fBody})";

        var gParam = _ctx.Fresh();
        var gScope = scope.Extend(gParam, ExprType.Int);
        var gBody = _exprs.GenInt(gScope, depth - 1);
        var g = $"(fn [[{gParam} : Int]] {gBody})";

        var x = _exprs.GenInt(scope, depth - 1);
        return $"(compose {f} {g} {x})";
    }
}
