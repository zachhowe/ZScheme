using System.Text;

namespace ZScheme.Fuzzer.Generation;

public enum StdlibImport { Option, List, Result, Array, Map }

// Builds expressions that use stdlib generic types (Option, List, Result, Array, Map)
// and reduces them back to Int so they can appear inside the Int-typed compute body.
// All values constructed here are statically safe — no runtime raise paths
// (no option/unwrap on None, no result/unwrap on Err, no list/head on empty list,
// no out-of-bounds array/nth, no map/get on a typed `None` result without unwrap-or).
public sealed class StdlibImportGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibImportGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Per-case: pick a random subset of stdlib modules and register them.
    public void ChooseImports()
    {
        if (_ctx.Rng.NextDouble() < 0.6)  _ctx.Imports.Add(StdlibImport.Option);
        if (_ctx.Rng.NextDouble() < 0.5)  _ctx.Imports.Add(StdlibImport.List);
        if (_ctx.Rng.NextDouble() < 0.4)  _ctx.Imports.Add(StdlibImport.Result);
        if (_ctx.Rng.NextDouble() < 0.5)  _ctx.Imports.Add(StdlibImport.Array);
        if (_ctx.Rng.NextDouble() < 0.35) _ctx.Imports.Add(StdlibImport.Map);
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

    // Reduces an (Array Int) back to an Int. Two shapes:
    //   (array/count (array e1 e2 ...))
    //   (array/fold (array e1 e2 ...) <init> (fn [[acc : Int] [x : Int]] body))
    // Always emits >=1 element so the element type is pinned at Int without needing
    // a type annotation.
    public string ReduceArrayToInt(Scope scope, int depth)
    {
        var n = 1 + _ctx.Rng.Next(5); // 1..5 elements
        var elems = new List<string>();
        for (var i = 0; i < n; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        var arrExpr = $"(array {string.Join(" ", elems)})";

        if (_ctx.Rng.NextDouble() < 0.4)
            return $"(array/count {arrExpr})";

        var init = _exprs.GenInt(scope, depth - 1);
        var acc = _ctx.Fresh();
        var x = _ctx.Fresh();
        var lamScope = scope.Extend(acc, ExprType.Int).Extend(x, ExprType.Int);
        var lamBody = _exprs.GenInt(lamScope, depth - 1);
        return $"(array/fold {arrExpr} {init} (fn [[{acc} : Int] [{x} : Int]] {lamBody}))";
    }

    // Composed Array reducer: (array/fold (array/map <arr> <f>) <init> <g>).
    // Exercises chained-lambda invocation through two higher-order stdlib calls.
    public string ReduceArrayMapFoldToInt(Scope scope, int depth)
    {
        var n = 1 + _ctx.Rng.Next(4); // 1..4 source elements
        var elems = new List<string>();
        for (var i = 0; i < n; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        var arrExpr = $"(array {string.Join(" ", elems)})";

        var mapParam = _ctx.Fresh();
        var mapBodyScope = scope.Extend(mapParam, ExprType.Int);
        var mapBody = _exprs.GenInt(mapBodyScope, depth - 1);
        var mapLam = $"(fn [[{mapParam} : Int]] {mapBody})";

        var init = _exprs.GenInt(scope, depth - 1);
        var acc = _ctx.Fresh();
        var x = _ctx.Fresh();
        var foldScope = scope.Extend(acc, ExprType.Int).Extend(x, ExprType.Int);
        var foldBody = _exprs.GenInt(foldScope, depth - 1);
        var foldLam = $"(fn [[{acc} : Int] [{x} : Int]] {foldBody})";

        return $"(array/fold (array/map {arrExpr} {mapLam}) {init} {foldLam})";
    }

    // Reduces a (Map String Int) back to an Int. Two shapes:
    //   (map/count (map-of (pair "k0" v0) ...))
    //   (option/unwrap-or (map/get <map> <key>) <default>)   [requires Option]
    // Always emits >=1 entry so the map's ^k / ^v type vars are pinned.
    public string ReduceMapToInt(Scope scope, int depth)
    {
        var n = 1 + _ctx.Rng.Next(4); // 1..4 entries
        var pairs = new List<string>();
        var keys = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var key = QuotedShortString();
            keys.Add(key);
            var value = _exprs.GenInt(scope, depth - 1);
            pairs.Add($"(pair {key} {value})");
        }
        var mapExpr = $"(map-of {string.Join(" ", pairs)})";

        var useCount = _ctx.Rng.NextDouble() < 0.4;
        if (useCount || !_ctx.Imports.Contains(StdlibImport.Option))
            return $"(map/count {mapExpr})";

        // Use map/get — returns (Option Int). Unwrap via option/unwrap-or.
        // 50% look up a known-present key, 50% a fresh (likely absent) key.
        var lookupKey = _ctx.Rng.NextDouble() < 0.5
            ? keys[_ctx.Rng.Next(keys.Count)]
            : QuotedShortString();
        var def = _exprs.GenInt(scope, depth - 1);
        return $"(option/unwrap-or (map/get {mapExpr} {lookupKey}) {def})";
    }

    // Bool-reducer: (map/contains-key? <map> <key>).
    public string ReduceMapContainsToBool(Scope scope, int depth)
    {
        var n = 1 + _ctx.Rng.Next(4);
        var pairs = new List<string>();
        var keys = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var key = QuotedShortString();
            keys.Add(key);
            pairs.Add($"(pair {key} {_exprs.GenInt(scope, depth - 1)})");
        }
        var mapExpr = $"(map-of {string.Join(" ", pairs)})";
        var lookupKey = _ctx.Rng.NextDouble() < 0.5
            ? keys[_ctx.Rng.Next(keys.Count)]
            : QuotedShortString();
        return $"(map/contains-key? {mapExpr} {lookupKey})";
    }

    // Builds a quoted 1-3 char lowercase-ASCII key literal. Safe alphabet means
    // no escape handling is needed inside the "..." form.
    private string QuotedShortString()
    {
        var len = 1 + _ctx.Rng.Next(3); // 1..3 chars
        var sb = new StringBuilder("\"");
        for (var i = 0; i < len; i++)
            sb.Append((char)('a' + _ctx.Rng.Next(26)));
        sb.Append('"');
        return sb.ToString();
    }
}
