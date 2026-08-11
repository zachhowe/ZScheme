using System.Globalization;

namespace ZScheme.Fuzzer.Generation;

// Emits body-level `(define (f ...) ...)` forms — a definition nested inside another function's
// body rather than at the top level.
//
// AstBuilder desugars a run of adjacent body-level defines into a single `letrec` group whose body
// is the rest of the sequence, so this shares LetrecLifter's lowering with LetrecExprGenerator.
// What it probes that `letrec` does not is the *desugar*: which forms end up in which group, and
// what each group's body is. Those choices are invisible in the surface syntax and easy to get
// subtly wrong — a define that scopes over one form too few, or two adjacent defines split into
// separate groups, both still compile and only show up as a wrong answer or a resolution failure.
//
// Seven shapes:
//   Self         — one define calling itself; the basic lifted-function path.
//   Mutual       — two adjacent defines calling each other, which only works if the desugar puts
//                  them in one group.
//   Capture      — a define closing over an enclosing binding, which becomes a capture parameter.
//   ValueAndFunc — `(define v ...)` and `(define (f ...) ...)` in one group, so the group mixes
//                  an ordinary local with a lifted function.
//   MidBody      — an expression first, then a group: the group must scope over the rest of the
//                  body and not over the expression before it.
//   TwoGroups    — two groups in one body where the second calls the first, so the first group's
//                  names must still resolve after its own group closes.
//   ValuePos     — the define left in scope as an IntFn so later generation can pass it around,
//                  which routes through IrNode.Closure instead of a direct call.
//
// The body a define needs is supplied by a randomly chosen wrapper — `begin`, `let`, or an
// immediately-invoked `lambda`. All three route through AstBuilder.BuildExprSequence, but they
// reach it from different callers, and those callers each used to fold their bodies by hand.
//
// Termination is by construction and enforced *inside* the function, for the same reason as
// LetrecExprGenerator: the define is put into Scope as an IntFn, so GenIntFnApply and GenIntFnArg
// can call it with an arbitrary Int that this generator never sees. A non-tail-recursive shape
// reached with Int.MaxValue would overflow the stack and kill the fuzzer process outright rather
// than being reported as a case failure. Depth is therefore capped at RecursionBound + 1 for every
// input. The precise TCO guarantee is asserted in Integration/NestedDefineTests.cs instead.
public sealed class NestedDefineExprGenerator
{
    private const int RecursionBound = 16;

    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public NestedDefineExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public string NestedDefineToInt(Scope scope, int depth)
    {
        return Gen(ExprType.Int, scope, depth);
    }

    public string NestedDefineToBool(Scope scope, int depth)
    {
        return Gen(ExprType.Bool, scope, depth);
    }

    private string Gen(ExprType resultType, Scope scope, int depth)
    {
        // See LetrecExprGenerator.Gen: inside a method body a group that reads instance state
        // becomes a private method of the class, which has no delegate form, so leaving a member
        // in value position there is a compile error by design rather than a bug to find.
        var shapes = _ctx.InInstanceContext ? 6 : 7;
        return _ctx.Rng.Next(shapes) switch
        {
            0 => Mutual(resultType, scope, depth),
            1 => Capture(resultType, scope, depth),
            2 => ValueAndFunc(resultType, scope, depth),
            3 => MidBody(resultType, scope, depth),
            4 => TwoGroups(resultType, scope, depth),
            5 when shapes == 7 => ValuePosition(resultType, scope, depth),
            _ => SelfRecursive(resultType, scope, depth),
        };
    }

    // (begin (define (f [n : Int]) : Int <clamped>) <body using f>)
    private string SelfRecursive(ExprType resultType, Scope scope, int depth)
    {
        var f = _ctx.Fresh();
        var n = _ctx.Fresh();
        var innerScope = scope.Extend(n, ExprType.Int);
        var baseExpr = _exprs.GenInt(innerScope, BodyDepth(depth));

        var recCall = $"({f} (- {n} 1))";
        // Half the time the recursive call is in tail position, so the lifted function becomes a
        // TCO loop; the other half keeps it a genuine recursive call.
        var step =
            _ctx.Rng.NextDouble() < 0.5
                ? recCall
                : $"(+ {_exprs.GenInt(innerScope, BodyDepth(depth))} {recCall})";

        var bodyScope = scope.Extend(f, ExprType.IntFn);
        return Wrap(
            resultType,
            scope,
            [Define(f, n, baseExpr, step)],
            Body(resultType, $"({f} {SmallCount()})", bodyScope, depth),
            depth
        );
    }

    // Two defines that call each other. Adjacent, so the desugar has to group them together —
    // one group per define would leave the second name unbound in the first's body.
    private string Mutual(ExprType resultType, Scope scope, int depth)
    {
        var a = _ctx.Fresh();
        var b = _ctx.Fresh();
        var n = _ctx.Fresh();
        var innerScope = scope.Extend(n, ExprType.Int);

        string One(string self, string other) =>
            Define(self, n, _exprs.GenInt(innerScope, BodyDepth(depth)), $"({other} (- {n} 1))");

        var bodyScope = scope.Extend(a, ExprType.IntFn).Extend(b, ExprType.IntFn);
        return Wrap(
            resultType,
            scope,
            [One(a, b), One(b, a)],
            Body(resultType, $"({a} {SmallCount()})", bodyScope, depth),
            depth
        );
    }

    // The define closes over a binding from the enclosing scope, which LetrecLifter must turn into
    // a leading capture parameter and pass at every call site.
    private string Capture(ExprType resultType, Scope scope, int depth)
    {
        var outer = _ctx.Fresh();
        var f = _ctx.Fresh();
        var n = _ctx.Fresh();
        var outerValue = _exprs.GenInt(scope, BodyDepth(depth));
        var capturedScope = scope.Extend(outer, ExprType.Int);
        var innerScope = capturedScope.Extend(n, ExprType.Int);

        var def = Define(
            f,
            n,
            _exprs.GenInt(innerScope, BodyDepth(depth)),
            $"(+ {outer} ({f} (- {n} 1)))"
        );

        var bodyScope = capturedScope.Extend(f, ExprType.IntFn);
        // The captured binding is introduced by the wrapper's own `let`, so the define sits in a
        // body where `outer` is an enclosing local rather than a parameter.
        return $"(let ([{outer} {outerValue}]) "
            + $"{Wrap(resultType, capturedScope, [def], Body(resultType, $"({f} {SmallCount()})", bodyScope, depth), depth)})";
    }

    // A value define and a function define in one group. The value is an ordinary local while the
    // function is lifted, so the group's site emission has to handle both at once.
    private string ValueAndFunc(ExprType resultType, Scope scope, int depth)
    {
        var v = _ctx.Fresh();
        var f = _ctx.Fresh();
        var n = _ctx.Fresh();
        var valueScope = scope.Extend(v, ExprType.Int);
        var innerScope = valueScope.Extend(n, ExprType.Int);

        // Value first: a later binding may read an earlier one, but not the reverse — the
        // initialization checker rejects that, so the order here is load-bearing.
        var valueDef = $"(define {v} {_exprs.GenInt(scope, BodyDepth(depth))})";
        var funcDef = Define(
            f,
            n,
            _exprs.GenInt(innerScope, BodyDepth(depth)),
            $"(+ {v} ({f} (- {n} 1)))"
        );

        var bodyScope = valueScope.Extend(f, ExprType.IntFn);
        return Wrap(
            resultType,
            scope,
            [valueDef, funcDef],
            Body(resultType, $"({f} {SmallCount()})", bodyScope, depth),
            depth
        );
    }

    // An expression, then a group. The group scopes over the rest of the body only — the leading
    // expression must stay outside it, as a discarded binding.
    private string MidBody(ExprType resultType, Scope scope, int depth)
    {
        var f = _ctx.Fresh();
        var n = _ctx.Fresh();
        var innerScope = scope.Extend(n, ExprType.Int);
        var def = Define(f, n, _exprs.GenInt(innerScope, BodyDepth(depth)), $"({f} (- {n} 1))");

        var bodyScope = scope.Extend(f, ExprType.IntFn);
        return Wrap(
            resultType,
            scope,
            [_exprs.GenInt(scope, BodyDepth(depth)), def],
            Body(resultType, $"({f} {SmallCount()})", bodyScope, depth),
            depth
        );
    }

    // Two groups separated by an expression, where the second group's define calls the first's.
    // The first group's names have to stay in scope for the whole rest of the body.
    private string TwoGroups(ExprType resultType, Scope scope, int depth)
    {
        var a = _ctx.Fresh();
        var b = _ctx.Fresh();
        var n = _ctx.Fresh();
        var m = _ctx.Fresh();

        var aDef = Define(
            a,
            n,
            _exprs.GenInt(scope.Extend(n, ExprType.Int), BodyDepth(depth)),
            $"({a} (- {n} 1))"
        );
        var aScope = scope.Extend(a, ExprType.IntFn);
        // `b` calls `a`, which was bound by the earlier group.
        var bDef = Define(
            b,
            m,
            _exprs.GenInt(aScope.Extend(m, ExprType.Int), BodyDepth(depth)),
            $"(+ ({a} {SmallCount()}) ({b} (- {m} 1)))"
        );

        var bodyScope = aScope.Extend(b, ExprType.IntFn);
        return Wrap(
            resultType,
            scope,
            [aDef, _exprs.GenInt(aScope, BodyDepth(depth)), bDef],
            Body(resultType, $"({b} {SmallCount()})", bodyScope, depth),
            depth
        );
    }

    // Leaves the define in scope as an IntFn without calling it here, so the surrounding expression
    // generator can pass it around as a value. That routes through IrNode.Closure — a native lambda
    // on C#, a display class on IL — rather than a direct call to the lifted function.
    private string ValuePosition(ExprType resultType, Scope scope, int depth)
    {
        var f = _ctx.Fresh();
        var n = _ctx.Fresh();
        var innerScope = scope.Extend(n, ExprType.Int);
        var def = Define(f, n, _exprs.GenInt(innerScope, BodyDepth(depth)), $"({f} (- {n} 1))");

        var bodyScope = scope.Extend(f, ExprType.IntFn);
        var body =
            resultType == ExprType.Bool
                ? _exprs.GenBool(bodyScope, BodyDepth(depth))
                : _exprs.GenInt(bodyScope, BodyDepth(depth));
        return Wrap(resultType, scope, [def], body, depth);
    }

    // Builds `(define (f [n : Int]) : Int (if (<= n 0) base (if (> n Bound) (f Bound) step)))`.
    // See the file header for why the clamp lives inside the function rather than at the call site.
    private static string Define(string name, string param, string baseExpr, string step)
    {
        return $"(define ({name} [{param} : Int]) : Int "
            + $"(if (<= {param} 0) {baseExpr} "
            + $"(if (> {param} {RecursionBound}) ({name} {RecursionBound}) {step})))";
    }

    // Puts the forms into a body. A define is only legal where there is a sequence of forms, and
    // the three wrappers reach AstBuilder.BuildExprSequence from three different callers — `begin`,
    // a `let` body, and a `lambda` body — each of which used to fold its forms separately.
    private string Wrap(
        ExprType resultType,
        Scope scope,
        IReadOnlyList<string> forms,
        string body,
        int depth
    )
    {
        var joined = string.Join(" ", forms);
        var kind = _ctx.Rng.Next(3);
        if (kind == 0)
            return $"(begin {joined} {body})";

        // `let` and `lambda` need a binding. It is read so the unused-binding analyzer stays quiet,
        // but only through `(* 0 n)`, which is always 0 and cannot overflow — so the wrapper never
        // changes the program's value regardless of what the binding's initializer computed.
        var name = _ctx.Fresh();
        var outerValue = _exprs.GenInt(scope, BodyDepth(depth));
        var neutralized =
            resultType == ExprType.Bool
                ? $"(if (= (* 0 {name}) 0) {body} #f)"
                : $"(+ (* 0 {name}) {body})";

        return kind == 1
            ? $"(let ([{name} {outerValue}]) {joined} {neutralized})"
            : $"((lambda ([{name} : Int]) {joined} {neutralized}) {outerValue})";
    }

    // Folds the group's result into a larger expression of the requested type, so it participates
    // in the program's value rather than being the whole of it.
    private string Body(ExprType resultType, string call, Scope bodyScope, int depth)
    {
        if (resultType == ExprType.Bool)
            return $"(> {call} {_exprs.GenInt(bodyScope, BodyDepth(depth))})";
        return $"(+ {call} {_exprs.GenInt(bodyScope, BodyDepth(depth))})";
    }

    // Recursion counts stay small: each step already costs a lifted call, and the differential
    // oracle bounds total program runtime.
    private string SmallCount()
    {
        return _ctx.Rng.Next(0, 11).ToString(CultureInfo.InvariantCulture);
    }

    private static int BodyDepth(int depth)
    {
        return Math.Max(0, Math.Min(depth - 1, 2));
    }
}
