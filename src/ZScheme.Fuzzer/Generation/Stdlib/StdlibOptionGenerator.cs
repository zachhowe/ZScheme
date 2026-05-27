namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions that exercise stdlib/option exports and reduces them
// to Int (or Bool, for the predicate forms). Every emitted (Option ^a) value
// is built from `(Some <int>)` so ^a unifies to Int without requiring an
// expression-level annotation.
public sealed class StdlibOptionGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibOptionGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported()
    {
        return _ctx.Imports.Contains(StdlibImport.Option);
    }

    // (option/unwrap-or (Some v) d) — existing shape.
    public string UnwrapOrToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d = _exprs.GenInt(scope, depth - 1);
        return $"(option/unwrap-or (Some {v}) {d})";
    }

    // (match (Some v) [(Some x) body] [None d]) — existing shape.
    public string MatchSomeNoneToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d = _exprs.GenInt(scope, depth - 1);
        var x = _ctx.Fresh();
        var armScope = scope.Extend(x, ExprType.Int);
        var armBody = _exprs.GenInt(armScope, depth - 1);
        return $"(match (Some {v}) [(Some {x}) {armBody}] [None {d}])";
    }

    // (option/unwrap (Some v)) — always succeeds because we always pass Some.
    // Exercises the unwrap codegen path even though it would raise on None.
    public string UnwrapToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        return $"(option/unwrap (Some {v}))";
    }

    // (option/unwrap-or (option/map (Some v) (lambda ([x : Int]) body)) d).
    // option/map preserves Some-ness so unwrap-or returns the mapped body.
    public string MapThenUnwrapOrToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d = _exprs.GenInt(scope, depth - 1);
        var x = _ctx.Fresh();
        var bodyScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenInt(bodyScope, depth - 1);
        return $"(option/unwrap-or (option/map (Some {v}) (lambda ([{x} : Int]) {body})) {d})";
    }

    // (option/unwrap-or (option/flat-map (Some v) (lambda ([x : Int]) (Some body))) d).
    public string FlatMapThenUnwrapOrToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d = _exprs.GenInt(scope, depth - 1);
        var x = _ctx.Fresh();
        var bodyScope = scope.Extend(x, ExprType.Int);
        var body = _exprs.GenInt(bodyScope, depth - 1);
        return $"(option/unwrap-or (option/flat-map (Some {v}) (lambda ([{x} : Int]) (Some {body}))) {d})";
    }

    // (option/some? (Some v)) — Bool-typed reducer.
    public string SomePredicateToBool(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        return $"(option/some? (Some {v}))";
    }

    // (option/none? (Some v)) — Bool-typed reducer.
    public string NonePredicateToBool(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        return $"(option/none? (Some {v}))";
    }

    public bool CanNestOptionResult()
    {
        return IsImported() && _ctx.Imports.Contains(StdlibImport.Result);
    }

    // (let [r : (Option (Result Int String)) (Some (Ok v))] (match r ...))
    public string NestedOptionResultToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d1 = _exprs.GenInt(scope, depth - 1);
        var d2 = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();
        var okArmVar = _ctx.Fresh();
        var armScope = scope.Extend(okArmVar, ExprType.Int);
        var okBody = _exprs.GenInt(armScope, depth - 1);

        return $"(let [{r} : (Option (Result Int String)) (Some (Ok {v}))]\n" +
               $"    (match {r} [(Some (Ok {okArmVar})) {okBody}] [(Some (Err _)) {d1}] [None {d2}]))";
    }

    // (let [r : (Option (Option Int)) (Some (Some v))] (match r ...))
    public string NestedOptionOptionToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d1 = _exprs.GenInt(scope, depth - 1);
        var d2 = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();
        var armVar = _ctx.Fresh();
        var armScope = scope.Extend(armVar, ExprType.Int);
        var body = _exprs.GenInt(armScope, depth - 1);

        return $"(let [{r} : (Option (Option Int)) (Some (Some {v}))]\n" +
               $"    (match {r} [(Some (Some {armVar})) {body}] [(Some None) {d1}] [None {d2}]))";
    }

    // (let [r : (Result (Option Int) String) (Ok (Some v))] (match r ...))
    public string NestedResultOptionToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d1 = _exprs.GenInt(scope, depth - 1);
        var d2 = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();
        var armVar = _ctx.Fresh();
        var armScope = scope.Extend(armVar, ExprType.Int);
        var body = _exprs.GenInt(armScope, depth - 1);

        return $"(let [{r} : (Result (Option Int) String) (Ok (Some {v}))]\n" +
               $"    (match {r} [(Ok (Some {armVar})) {body}] [(Ok None) {d1}] [(Err _) {d2}]))";
    }

    // (let [r : (Option (Result (Option Int) String)) (Some (Ok (Some v)))] (match r ...))
    public string TripleNestedOptionResultToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d1 = _exprs.GenInt(scope, depth - 1);
        var d2 = _exprs.GenInt(scope, depth - 1);
        var d3 = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();
        var armVar = _ctx.Fresh();
        var armScope = scope.Extend(armVar, ExprType.Int);
        var body = _exprs.GenInt(armScope, depth - 1);

        return $"(let [{r} : (Option (Result (Option Int) String)) (Some (Ok (Some {v})))]\n" +
               $"    (match {r} [(Some (Ok (Some {armVar}))) {body}] " +
               $"[(Some (Ok None)) {d1}] [(Some (Err _)) {d2}] [None {d3}]))";
    }
}
