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

    public bool IsImported()
    {
        return _ctx.Imports.Contains(StdlibImport.Core);
    }

    // (id <int>) — ^a unifies to Int from the argument.
    public string IdToInt(Scope scope, int depth)
    {
        return $"(id {_exprs.GenInt(scope, depth - 1)})";
    }

    // ((compose (lambda ([x : Int]) body1) (lambda ([y : Int]) body2)) <int>)
    // ^a, ^b, ^c all instantiate at Int. `compose` takes exactly two functions
    // and returns their composition, which is then applied to the Int argument
    // (core exports `compose` but not the 3-arg `compose/call`).
    public string ComposeToInt(Scope scope, int depth)
    {
        var fParam = _ctx.Fresh();
        var fScope = scope.Extend(fParam, ExprType.Int);
        var fBody = _exprs.GenInt(fScope, depth - 1);
        var f = $"(lambda ([{fParam} : Int]) {fBody})";

        var gParam = _ctx.Fresh();
        var gScope = scope.Extend(gParam, ExprType.Int);
        var gBody = _exprs.GenInt(gScope, depth - 1);
        var g = $"(lambda ([{gParam} : Int]) {gBody})";

        var x = _exprs.GenInt(scope, depth - 1);
        return $"((compose {f} {g}) {x})";
    }

    // (if (is-null? <operand>) 1 0). `is-null?` lowers to
    // (System.Object/ReferenceEquals x null); for the boxed-value-type operand
    // (an Int) the boxing path differs between the C# and IL backends (see the
    // file header), so this is a deliberately suspected-divergent probe, gated
    // low by ProgramGenerator's EnableNullChecks flag. Operands cover: the null
    // literal (both backends → true), a String (reference type → false), and an
    // Int (boxed value type → the divergence-suspect case).
    public string IsNullCheckToInt(Scope scope, int depth)
    {
        var operand = _ctx.Rng.Next(3) switch
        {
            0 => "null",
            1 => _exprs.GenString(scope, depth - 1),
            _ => _exprs.GenInt(scope, depth - 1),
        };
        return $"(if (is-null? {operand}) 1 0)";
    }
}
