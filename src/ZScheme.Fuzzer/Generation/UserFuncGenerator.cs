namespace ZScheme.Fuzzer.Generation;

public sealed class UserFuncGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;
    private readonly WhereConstraintGenerator _where;

    // Default ground-type set for non-generic functions — they're always called
    // with Int at the one position, so this is a single-element set.
    private static readonly IReadOnlySet<ExprType> OnlyInt = new HashSet<ExprType> { ExprType.Int };

    public UserFuncGenerator(GeneratorContext ctx, ExprGenerator exprs, WhereConstraintGenerator where)
    {
        _ctx = ctx;
        _exprs = exprs;
        _where = where;
    }

    public UserFunc GenerateUserFunction(string name)
    {
        var pick = _ctx.Rng.NextDouble();
        if (pick < 0.20) return GenerateRecursiveFunction(name);
        if (pick < 0.40) return GenerateHigherOrderFunction(name);
        if (pick < 0.65) return GenerateGenericFunction(name);
        return GenerateRegularFunction(name);
    }

    private UserFunc GenerateRegularFunction(string name)
    {
        var arity = 1 + _ctx.Rng.Next(2);
        var scope = new Scope();
        var paramNames = new List<string>();
        for (var i = 0; i < arity; i++)
        {
            var pname = _ctx.Fresh();
            paramNames.Add(pname);
            scope = scope.Extend(pname, ExprType.Int);
        }

        var body = _exprs.GenInt(scope, _ctx.MaxDepth);
        var paramStr = string.Join(" ", paramNames.Select(p => $"[{p} : Int]"));
        var def = $"(define ({name} {paramStr}) : Int\n  {body})";
        var paramTypes = Enumerable.Repeat(ExprType.Int, arity).ToList();
        var isGeneric = new bool[arity];
        return new UserFunc(name, UserFuncKind.Regular, paramTypes, def,
            OnlyInt, isGeneric, ReturnIsGeneric: false);
    }

    private UserFunc GenerateRecursiveFunction(string name)
    {
        var nParam = _ctx.Fresh();
        var accParam = _ctx.Fresh();
        var scope = new Scope()
            .Extend(nParam, ExprType.Int)
            .Extend(accParam, ExprType.Int);

        var bodyDepth = Math.Min(_ctx.MaxDepth, 3);
        var baseExpr = _exprs.GenInt(scope, bodyDepth);
        var stepExpr = _exprs.GenInt(scope, bodyDepth);

        var isTail = _ctx.Rng.NextDouble() < 0.75;
        var recCall = $"({name} (- {nParam} 1) {stepExpr})";
        var elseBranch = isTail ? recCall : $"(+ 1 {recCall})";
        var body = $"(if (<= {nParam} 0) {baseExpr} {elseBranch})";

        var def = $"(define ({name} [{nParam} : Int] [{accParam} : Int]) : Int\n  {body})";
        return new UserFunc(name, UserFuncKind.Recursive,
            [ExprType.Int, ExprType.Int], def,
            OnlyInt, [false, false], ReturnIsGeneric: false);
    }

    private UserFunc GenerateHigherOrderFunction(string name)
    {
        var fParam = _ctx.Fresh();
        var xParam = _ctx.Fresh();
        var scope = new Scope()
            .Extend(fParam, ExprType.IntFn)
            .Extend(xParam, ExprType.Int);

        var body = _exprs.GenInt(scope, _ctx.MaxDepth);
        var def = $"(define ({name} [{fParam} : (Int -> Int)] [{xParam} : Int]) : Int\n  {body})";
        return new UserFunc(name, UserFuncKind.HigherOrder,
            [ExprType.IntFn, ExprType.Int], def,
            OnlyInt, [false, false], ReturnIsGeneric: false);
    }

    // Emits a polymorphic function. Three shapes are chosen to exercise different
    // generic codegen paths:
    //   (define (id [x : ^a]) : ^a x)
    //   (define (const [x : ^a] [y : ^b]) : ^a x)
    //   (define (apply [f : (^a -> Int)] [x : ^a]) : Int (f x))
    // At call sites we instantiate ^a (and ^b) at any ground type compatible with
    // the chosen :where constraint — {Int, Bool, Float} for all supported
    // constraints since they're all value types. The returned UserFunc's
    // AllowedGrounds narrows the call-site ground-type picker.
    private UserFunc GenerateGenericFunction(string name)
    {
        var pick = _ctx.Rng.Next(3);
        // Int-compatible constraint flags only — see WhereConstraintGenerator.
        // Type-param list passed depends on the shape (id has only ^a; const has
        // ^a and ^b; apply has ^a only).
        var typeParams = pick == 1 ? new[] { "^a", "^b" } : ["^a"];
        // Bumped from 0.20 → 0.40 to deliberately stress the multi-constraint
        // path. WhereConstraintGenerator emits up to one constraint per type
        // param, so the two-param `const` shape gets the bulk of multi-clause
        // exercise here.
        var constraintSuffix = _where.MaybeEmit(typeParams, emitProbability: 0.40);

        // All three constraint variants (and the unconstrained case) admit the
        // same set of ground types from the fuzzer's perspective: value-type
        // primitives that round-trip cleanly back to Int.
        IReadOnlySet<ExprType> allowed = new HashSet<ExprType>
        {
            ExprType.Int, ExprType.Bool, ExprType.Float,
        };

        if (pick == 0)
        {
            // id : ^a -> ^a
            var p = _ctx.Fresh();
            var def = $"(define ({name} [{p} : ^a]) : ^a{constraintSuffix}\n  {p})";
            return new UserFunc(name, UserFuncKind.Generic,
                [ExprType.Int], def,
                allowed, [true], ReturnIsGeneric: true);
        }
        else if (pick == 1)
        {
            // const : ^a ^b -> ^a (second param instantiated at ^b = Int for simplicity)
            var p1 = _ctx.Fresh();
            var p2 = _ctx.Fresh();
            var def = $"(define ({name} [{p1} : ^a] [{p2} : ^b]) : ^a{constraintSuffix}\n  {p1})";
            return new UserFunc(name, UserFuncKind.Generic,
                [ExprType.Int, ExprType.Int], def,
                allowed, [true, false], ReturnIsGeneric: true);
        }
        else
        {
            // apply : (^a -> Int) ^a -> Int
            var pf = _ctx.Fresh();
            var px = _ctx.Fresh();
            var def = $"(define ({name} [{pf} : (^a -> Int)] [{px} : ^a]) : Int{constraintSuffix}\n  ({pf} {px}))";
            return new UserFunc(name, UserFuncKind.Generic,
                [ExprType.IntFn, ExprType.Int], def,
                allowed, [true, true], ReturnIsGeneric: false);
        }
    }
}
