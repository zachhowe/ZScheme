namespace ZScheme.Fuzzer.Generation;

public enum StdlibImport { Option, List, Result }

// Builds expressions that use stdlib generic types (Option, List, Result)
// and reduces them back to Int so they can appear inside the Int-typed compute body.
// All values constructed here are statically safe — no runtime raise paths
// (no option/unwrap on None, no result/unwrap on Err, no list/head on empty list).
public sealed class StdlibImportGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibImportGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Per-case: pick a random subset of the three stdlib modules and register them.
    public void ChooseImports()
    {
        if (_ctx.Rng.NextDouble() < 0.6) _ctx.Imports.Add(StdlibImport.Option);
        if (_ctx.Rng.NextDouble() < 0.5) _ctx.Imports.Add(StdlibImport.List);
        if (_ctx.Rng.NextDouble() < 0.4) _ctx.Imports.Add(StdlibImport.Result);
    }

    // Reduces an (Option Int) value back to an Int. Two shapes:
    //   (option/unwrap-or (Some <int>) <default-int>)
    //   (match (Some <int>) [(Some v) v] [None <default-int>])
    public string ReduceOptionToInt(Scope scope, int depth)
    {
        var useUnwrapOr = _ctx.Rng.NextDouble() < 0.5;
        var v = _exprs.GenInt(scope, depth - 1);
        var d = _exprs.GenInt(scope, depth - 1);
        if (useUnwrapOr)
        {
            // Note: using (Some v) guarantees ^a=Int is inferred. Using bare `None`
            // in the first position would require an annotation.
            return $"(option/unwrap-or (Some {v}) {d})";
        }
        else
        {
            var x = _ctx.Fresh();
            var armScope = scope.Extend(x, ExprType.Int);
            var armBody = _exprs.GenInt(armScope, depth - 1);
            return $"(match (Some {v}) [(Some {x}) {armBody}] [None {d}])";
        }
    }

    // Reduces a (List Int) back to an Int.
    //   (list/count (list e1 e2 ...))     — length
    //   (list/fold (list ...) init (fn [[acc : Int] [x : Int]] body))  — fold
    public string ReduceListToInt(Scope scope, int depth)
    {
        var n = _ctx.Rng.Next(6); // 0-5 elements
        var elems = new List<string>();
        for (var i = 0; i < n; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        var listExpr = elems.Count == 0 ? "(list)" : $"(list {string.Join(" ", elems)})";

        var useCount = _ctx.Rng.NextDouble() < 0.4;
        if (useCount)
        {
            return $"(list/count {listExpr})";
        }

        var init = _exprs.GenInt(scope, depth - 1);
        var acc = _ctx.Fresh();
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(acc, ExprType.Int).Extend(x, ExprType.Int);
        var lamBody = _exprs.GenInt(lamScope, depth - 1);
        return $"(list/fold {listExpr} {init} (fn [[{acc} : Int] [{x} : Int]] {lamBody}))";
    }

    // Reduces a (Result Int String) back to an Int via a match with both arms pinned.
    // Uses an annotated `let` binding so the free ^e parameter of Result is fixed
    // to String without needing `(: expr type)` expression-level annotation.
    public string ReduceResultToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();

        // Build (Ok v) here; matching binds _ for the Err arm (no type pin on ^e needed
        // because the let annotation forces (Result Int String)).
        var okArmVar = _ctx.Fresh();
        var armScope = scope.Extend(okArmVar, ExprType.Int);
        var okBody = _exprs.GenInt(armScope, depth - 1);

        return $"(let [{r} : (Result Int String) (Ok {v})]\n" +
               $"    (match {r} [(Ok {okArmVar}) {okBody}] [(Err _) {d}]))";
    }

    // Builds (Option (Result Int String)) and destructures via a nested match arm
    // — (Some (Ok x)) — to exercise nested-constructor decision-tree compilation in
    // PatternCompiler.cs. Uses a typed `let` binding so ^e of Result is fixed to
    // String without a type annotation on the expression.
    public bool CanNestOptionResult() =>
        _ctx.Imports.Contains(StdlibImport.Option) && _ctx.Imports.Contains(StdlibImport.Result);

    public string ReduceNestedOptionResultToInt(Scope scope, int depth)
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

    // Builds (Option (Option Int)) and destructures via nested ctor patterns.
    // Gate: Option import.
    public bool CanNestOptionOption() => _ctx.Imports.Contains(StdlibImport.Option);

    public string ReduceNestedOptionOptionToInt(Scope scope, int depth)
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

    // Builds (Result (Option Int) String) and destructures via nested ctor patterns.
    // Gate: Option AND Result imports.
    public string ReduceNestedResultOptionToInt(Scope scope, int depth)
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

    // Three-level nesting: (Option (Result (Option Int) String)).
    // Gate: Option AND Result imports.
    public string ReduceTripleNestedOptionResultToInt(Scope scope, int depth)
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
