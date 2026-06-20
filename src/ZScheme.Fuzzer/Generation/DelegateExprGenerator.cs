namespace ZScheme.Fuzzer.Generation;

// Exercises the `(delegate ...)` type form — explicit .NET delegate-type
// annotations on parameters, which bypass the compiler's default
// function->Func<>/Action<> mapping. The C# and IL backends emit this form
// very differently (C# wraps values in adapter lambdas / casts; IL emits a
// `newobj` on the delegate constructor), so it is fertile ground for the
// compile-consistency and differential-exec oracles.
//
// EmitHelpers() emits a small fixed set of top-level helper defines that take a
// delegate-typed parameter and invoke it; the reducers call those helpers,
// passing the delegate value three different ways to cover the distinct codegen
// paths:
//   * a correctly-arity'd lambda                  -> Func<int,int>
//   * a named function reference (adapter wrap)   -> Func<int,int>
//   * a zero-arg lambda                           -> Action (void-returning)
//
// A zero-arg lambda passed where Func<int,int> is expected is intentionally NOT
// generated: an arity-mismatched lambda against a concrete generic delegate is a
// type error (the unifier rejects it with a "Delegate/function shape mismatch"),
// so producing it would only yield programs that both backends correctly refuse
// to compile.
//
// Helper names are `fuzz-`-prefixed to stay clear of the xN identifier space the
// other generators use. The helpers are emitted only in the main module, so the
// reducers are gated (in ExprGenerator) on EnableDelegateForms && !InAuxModule.
public sealed class DelegateExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public DelegateExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Top-level helper block. Emitted once per program when EnableDelegateForms
    // is set. fuzz-run-action keeps the `: Unit` return shape of real-world
    // delegate sinks (cf. examples/delegate-example.zs); the reducer sequences
    // its Unit result with a generated Int via `begin`.
    public string EmitHelpers()
    {
        return string.Join(
            "\n",
            "(define (fuzz-run-func [f : (delegate System.Func<int,int>)]) : Int",
            "  (f 10))",
            "",
            "(define (fuzz-run-action [a : (delegate System.Action)]) : Unit",
            "  (a))",
            "",
            "(define (fuzz-deleg-fn [x : Int]) : Int",
            "  (* x 2))"
        );
    }

    // (fuzz-run-func (lambda ([x : Int]) <int-over-x>)) — unary lambda whose
    // arity matches the Func<int,int> delegate exactly.
    public string ReduceFuncDelegateLambdaToInt(Scope scope, int depth)
    {
        var p = _ctx.Fresh();
        var body = _exprs.GenInt(scope.Extend(p, ExprType.Int), depth - 1);
        return $"(fuzz-run-func (lambda ([{p} : Int]) {body}))";
    }

    // (fuzz-run-func fuzz-deleg-fn) — a named function passed where a delegate
    // is expected. Exercises the method-ref -> adapter-lambda wrapping path.
    public string ReduceFuncDelegateNamedToInt(Scope scope, int depth)
    {
        return "(fuzz-run-func fuzz-deleg-fn)";
    }

    // (begin (fuzz-run-action (lambda () ())) <int>) — passes a zero-arg,
    // Unit-returning lambda (`()` is the unit literal) as a System.Action, then
    // yields a generated Int so the result flows back into GenInt callers.
    public string ReduceActionToInt(Scope scope, int depth)
    {
        var tail = _exprs.GenInt(scope, depth - 1);
        return $"(begin (fuzz-run-action (lambda () ())) {tail})";
    }
}
