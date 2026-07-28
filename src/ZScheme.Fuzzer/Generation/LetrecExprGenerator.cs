using System.Globalization;

namespace ZScheme.Fuzzer.Generation;

// Emits `(letrec ([f (lambda ...)] ...) body)` shaped Int/Bool expressions.
//
// letrec is the only form whose bindings are all in scope in each other's values, and it is
// lowered very differently from let/let*: LetrecLifter rewrites the group into top-level static
// functions with their captures as leading parameters, replacing each binding with an
// IrNode.Closure. The two backends then diverge — C# emits static methods plus native lambdas,
// IL emits static methods plus synthesized display classes — so this is exactly the kind of form
// where a wrong capture set or a missed reference rewrite shows up as a backend disagreement.
//
// Five shapes, one per lowering path:
//   Self        — a self-recursive function; the basic lifted-function path.
//   Mutual      — a mutually-recursive pair; only expressible because siblings become direct
//                 calls on stable top-level names.
//   Capture     — a function closing over an enclosing local, which becomes a capture parameter.
//   Mixed       — value and function bindings together, which forces the site's emission order
//                 (a value binding must precede the closure that captures it).
//   ValuePos    — the function bound into scope as an IntFn so later expression generation can
//                 pass it around as a value, exercising the Closure path rather than direct calls.
//
// Termination is by construction, matching the contract UserFuncGenerator uses for recursive
// functions: every recursive parameter strictly decrements and bottoms out at `n <= 0`, and call
// sites pass a small Int literal. Depths stay small on purpose — a deep count would diverge
// between the backends if TCO ever fired on only one of them, and the differential oracle would
// report that as a runtime difference rather than the missing-TCO bug it actually is. The precise
// TCO guarantee is asserted in Integration/LetrecTests.cs instead.
public sealed class LetrecExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public LetrecExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public string LetrecToInt(Scope scope, int depth)
    {
        return Gen(ExprType.Int, scope, depth);
    }

    public string LetrecToBool(Scope scope, int depth)
    {
        return Gen(ExprType.Bool, scope, depth);
    }

    private string Gen(ExprType resultType, Scope scope, int depth)
    {
        return _ctx.Rng.Next(5) switch
        {
            0 => Mutual(resultType, scope, depth),
            1 => Capture(resultType, scope, depth),
            2 => Mixed(resultType, scope, depth),
            3 => ValuePosition(resultType, scope, depth),
            _ => SelfRecursive(resultType, scope, depth),
        };
    }

    // (letrec ([f (lambda ([n : Int]) : Int <clamped body>)]) body)
    // Half the time the recursive call is in tail position, so the lifted function becomes a TCO
    // loop; the other half keeps it a genuine recursive call.
    private string SelfRecursive(ExprType resultType, Scope scope, int depth)
    {
        var f = _ctx.Fresh();
        var n = _ctx.Fresh();
        var innerScope = scope.Extend(n, ExprType.Int);
        var baseExpr = _exprs.GenInt(innerScope, BodyDepth(depth));

        var recCall = $"({f} (- {n} 1))";
        var step =
            _ctx.Rng.NextDouble() < 0.5
                ? recCall
                : $"(+ {_exprs.GenInt(innerScope, BodyDepth(depth))} {recCall})";

        var bodyScope = scope.Extend(f, ExprType.IntFn);
        return $"(letrec ([{f} {Lambda(n, f, baseExpr, step)}]) "
            + $"{Body(resultType, f, bodyScope, depth)})";
    }

    // Two functions that call each other. Each hands off to the other after decrementing, so the
    // pair walks down to the base case together.
    private string Mutual(ExprType resultType, Scope scope, int depth)
    {
        var a = _ctx.Fresh();
        var b = _ctx.Fresh();
        var n = _ctx.Fresh();
        var innerScope = scope.Extend(n, ExprType.Int);

        string One(string self, string other) =>
            Lambda(n, self, _exprs.GenInt(innerScope, BodyDepth(depth)), $"({other} (- {n} 1))");

        var bodyScope = scope.Extend(a, ExprType.IntFn).Extend(b, ExprType.IntFn);
        return $"(letrec ([{a} {One(a, b)}] [{b} {One(b, a)}]) "
            + $"{Body(resultType, a, bodyScope, depth)})";
    }

    // The lambda closes over a binding from the enclosing scope, which LetrecLifter must turn
    // into a leading capture parameter and pass at the construction site.
    private string Capture(ExprType resultType, Scope scope, int depth)
    {
        var outer = _ctx.Fresh();
        var f = _ctx.Fresh();
        var n = _ctx.Fresh();
        var outerValue = _exprs.GenInt(scope, BodyDepth(depth));
        var capturedScope = scope.Extend(outer, ExprType.Int);
        var innerScope = capturedScope.Extend(n, ExprType.Int);

        var lambda = Lambda(
            n,
            f,
            _exprs.GenInt(innerScope, BodyDepth(depth)),
            $"(+ {outer} ({f} (- {n} 1)))"
        );

        var bodyScope = capturedScope.Extend(f, ExprType.IntFn);
        return $"(let ([{outer} {outerValue}]) "
            + $"(letrec ([{f} {lambda}]) {Body(resultType, f, bodyScope, depth)}))";
    }

    // A value binding, a function that captures it, and a value binding that calls that function.
    // Written function-first so the site cannot simply emit bindings in source order: `v` has to
    // be bound before the closure, and the closure before `r`.
    private string Mixed(ExprType resultType, Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var f = _ctx.Fresh();
        var r = _ctx.Fresh();
        var n = _ctx.Fresh();
        var innerScope = scope.Extend(v, ExprType.Int).Extend(n, ExprType.Int);

        var lambda = Lambda(
            n,
            f,
            _exprs.GenInt(innerScope, BodyDepth(depth)),
            $"(+ {v} ({f} (- {n} 1)))"
        );
        var bodyScope = scope
            .Extend(v, ExprType.Int)
            .Extend(f, ExprType.IntFn)
            .Extend(r, ExprType.Int);

        return $"(letrec ([{f} {lambda}] "
            + $"[{v} {_exprs.GenInt(scope, BodyDepth(depth))}] "
            + $"[{r} ({f} {SmallCount()})]) "
            + $"{Body(resultType, r, bodyScope, depth, alreadyInt: true)})";
    }

    // Binds the function and then leaves it in scope as an IntFn without calling it directly, so
    // the surrounding expression generator can pass it around as a value. That routes through
    // IrNode.Closure (a native lambda on C#, a display class on IL) rather than a direct call.
    private string ValuePosition(ExprType resultType, Scope scope, int depth)
    {
        var f = _ctx.Fresh();
        var n = _ctx.Fresh();
        var innerScope = scope.Extend(n, ExprType.Int);
        var lambda = Lambda(n, f, _exprs.GenInt(innerScope, BodyDepth(depth)), $"({f} (- {n} 1))");

        // The body is generated with `f` in scope as an IntFn; GenIntFnApply / GenIntFnArg pick
        // it up on their own, so the reference may end up in either call or value position.
        var bodyScope = scope.Extend(f, ExprType.IntFn);
        var body =
            resultType == ExprType.Bool
                ? _exprs.GenBool(bodyScope, BodyDepth(depth))
                : _exprs.GenInt(bodyScope, BodyDepth(depth));
        return $"(letrec ([{f} {lambda}]) {body})";
    }

    // Builds `(lambda ([n : Int]) : Int (if (<= n 0) base (if (> n Bound) (self Bound) step)))`.
    //
    // The clamp is what makes the recursion safe. Unlike the recursive functions
    // UserFuncGenerator emits — whose call sites are all under its control, so it can force a
    // small literal first argument — a letrec binding is put into Scope as an IntFn, which lets
    // GenIntFnApply and GenIntFnArg call it with an arbitrary Int. Bounding inside the function
    // instead of at the call site keeps it terminating for every input, including Int.MaxValue.
    // Depth is therefore at most Bound + 1 regardless of how the function is reached, so a
    // non-tail-recursive shape cannot overflow the stack — which would kill the fuzzer process
    // outright rather than being reported as a case failure.
    private string Lambda(string param, string self, string baseExpr, string step)
    {
        return $"(lambda ([{param} : Int]) : Int "
            + $"(if (<= {param} 0) {baseExpr} "
            + $"(if (> {param} {RecursionBound}) ({self} {RecursionBound}) {step})))";
    }

    private const int RecursionBound = 16;

    // Applies the group's entry point to a bounded literal and reduces to the requested type.
    private string Body(
        ExprType resultType,
        string entry,
        Scope bodyScope,
        int depth,
        bool alreadyInt = false
    )
    {
        var call = alreadyInt ? entry : $"({entry} {SmallCount()})";
        if (resultType == ExprType.Bool)
            return $"(> {call} {_exprs.GenInt(bodyScope, BodyDepth(depth))})";
        // Fold the call into a larger Int expression so the group's result actually participates
        // in the program's value rather than being the whole of it.
        return $"(+ {call} {_exprs.GenInt(bodyScope, BodyDepth(depth))})";
    }

    // Recursion counts stay small: the group already costs a lifted call per step, and the
    // differential oracle bounds total program runtime.
    private string SmallCount()
    {
        return _ctx.Rng.Next(0, 11).ToString(CultureInfo.InvariantCulture);
    }

    private int BodyDepth(int depth)
    {
        return Math.Max(0, Math.Min(depth - 1, 2));
    }
}
