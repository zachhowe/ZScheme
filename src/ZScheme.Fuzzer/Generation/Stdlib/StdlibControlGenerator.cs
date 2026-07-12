namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates expressions over the stdlib/control macros (when/unless) and the
// stdlib/catch macro.
//
// when/unless bodies must be Unit-typed (the macro unifies the body against
// the `()` branch of an `if`), so observability requires an effect: the shapes
// mutate a mutable-vector slot inside the conditional and read it back after
// (gated on the MutableVector import). A pure `(begin (when t ()) k)` variant
// exercises the Unit-branch codegen without needing an effect.
//
// catch expands textually at the use site into a with-handlers producing
// `(Result T Error)` — the expansion references Err/Error/None/__ex-message in
// the *user* module, so the Catch import force-adds Result+Error+Option
// (see StdlibImportGenerator).
public sealed class StdlibControlGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibControlGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool ControlImported()
    {
        return _ctx.Imports.Contains(StdlibImport.Control);
    }

    public bool CatchImported()
    {
        return _ctx.Imports.Contains(StdlibImport.Catch);
    }

    public bool CanMutateEffect()
    {
        return ControlImported()
            && _ctx.Imports.Contains(StdlibImport.MutableVector)
            && _ctx.Imports.Contains(StdlibImport.Vector);
    }

    // (let ([v (vector->mutable-vector (vector e1 e2))])
    //   (begin (when <bool> (vector-set! v idx <int>)) (vector-ref v idx)))
    // — the conditional effect decides which value the read-back observes.
    public string WhenMutateToInt(Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var count = 1 + _ctx.Rng.Next(3);
        var elems = new List<string>(count);
        for (var i = 0; i < count; i++)
            elems.Add(_exprs.GenInt(scope, depth - 1));
        var idx = _ctx.Rng.Next(count);
        var form = _ctx.Rng.NextDouble() < 0.5 ? "when" : "unless";
        var cond = _exprs.GenBool(scope, depth - 1);
        var newVal = _exprs.GenInt(scope, depth - 1);
        return $"(let ([{v} (vector->mutable-vector (vector {string.Join(" ", elems)}))]) "
            + $"(begin ({form} {cond} (vector-set! {v} {idx} {newVal})) (vector-ref {v} {idx})))";
    }

    // (begin (when <bool> ()) <int>) — pure Unit-branch coverage; also emits
    // multi-expression bodies (the macro's `body ...` ellipsis) at times.
    public string WhenUnitToInt(Scope scope, int depth)
    {
        var form = _ctx.Rng.NextDouble() < 0.5 ? "when" : "unless";
        var cond = _exprs.GenBool(scope, depth - 1);
        var body = _ctx.Rng.NextDouble() < 0.3 ? "() ()" : "()";
        var k = _exprs.GenInt(scope, depth - 1);
        return $"(begin ({form} {cond} {body}) {k})";
    }

    // (match (catch <int-expr>) [(Ok v) v] [(Err e) <fallback>]) — the inner
    // expression may throw naturally (division shapes inside GenInt) or via an
    // explicit raise; either way catch converts it to a Result.
    public string CatchToInt(Scope scope, int depth)
    {
        string inner;
        if (_ctx.Rng.NextDouble() < 0.35)
        {
            // Guaranteed-throwing branch mirror of UseExprGenerator's idiom.
            var cond = _exprs.GenBool(scope, depth - 1);
            var ok = _exprs.GenInt(scope, depth - 1);
            inner =
                $"(if {cond} {ok} (raise (new System.InvalidOperationException \"fuzz-catch\")))";
        }
        else
        {
            inner = _exprs.GenInt(scope, depth - 1);
        }

        var okVar = _ctx.Fresh();
        var errVar = _ctx.Fresh();
        var fallback = _exprs.GenInt(scope, depth - 1);
        return $"(match (catch {inner}) [(Ok {okVar}) {okVar}] [(Err {errVar}) {fallback}])";
    }
}
