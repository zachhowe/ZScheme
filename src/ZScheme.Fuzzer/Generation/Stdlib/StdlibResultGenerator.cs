namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over (Result Int String) values built from `(Ok <int>)`
// and reduces them back to Int. The ^e parameter is fixed to String via a typed
// `let` binding so no expression-level annotation is needed at the constructor.
public sealed class StdlibResultGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibResultGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported() => _ctx.Imports.Contains(StdlibImport.Result);

    // (let [r : (Result Int String) (Ok v)] (match r [(Ok x) body] [(Err _) d]))
    public string MatchOkErrToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();

        var okArmVar = _ctx.Fresh();
        var armScope = scope.Extend(okArmVar, ExprType.Int);
        var okBody = _exprs.GenInt(armScope, depth - 1);

        return $"(let [{r} : (Result Int String) (Ok {v})]\n" +
               $"    (match {r} [(Ok {okArmVar}) {okBody}] [(Err _) {d}]))";
    }

    // (let [r : (Result Int String) (Ok v)] (result/unwrap r))
    public string UnwrapToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();
        return $"(let [{r} : (Result Int String) (Ok {v})] (result/unwrap {r}))";
    }

    // (let [r ...] (match (result/map r (fn ...)) [(Ok x) body] [(Err _) d]))
    public string MapThenMatchToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();
        var x = _ctx.Fresh();
        var mapScope = scope.Extend(x, ExprType.Int);
        var mapBody = _exprs.GenInt(mapScope, depth - 1);
        var armVar = _ctx.Fresh();
        var armScope = scope.Extend(armVar, ExprType.Int);
        var armBody = _exprs.GenInt(armScope, depth - 1);

        return $"(let [{r} : (Result Int String) (Ok {v})]\n" +
               $"    (match (result/map {r} (fn [[{x} : Int]] {mapBody})) " +
               $"[(Ok {armVar}) {armBody}] [(Err _) {d}]))";
    }

    // (let [r ...] (match (result/flat-map r (fn ... (Ok body))) ...))
    public string FlatMapThenMatchToInt(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var d = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();
        var x = _ctx.Fresh();
        var fmScope = scope.Extend(x, ExprType.Int);
        var fmBody = _exprs.GenInt(fmScope, depth - 1);
        var armVar = _ctx.Fresh();
        var armScope = scope.Extend(armVar, ExprType.Int);
        var armBody = _exprs.GenInt(armScope, depth - 1);

        return $"(let [{r} : (Result Int String) (Ok {v})]\n" +
               $"    (match (result/flat-map {r} (fn [[{x} : Int]] (Ok {fmBody}))) " +
               $"[(Ok {armVar}) {armBody}] [(Err _) {d}]))";
    }

    // (let [r ...] (result/ok? r)) — Bool-typed reducer.
    public string OkPredicateToBool(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();
        return $"(let [{r} : (Result Int String) (Ok {v})] (result/ok? {r}))";
    }

    public string ErrPredicateToBool(Scope scope, int depth)
    {
        var v = _exprs.GenInt(scope, depth - 1);
        var r = _ctx.Fresh();
        return $"(let [{r} : (Result Int String) (Ok {v})] (result/err? {r}))";
    }
}
