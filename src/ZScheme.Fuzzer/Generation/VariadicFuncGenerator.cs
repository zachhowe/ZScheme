namespace ZScheme.Fuzzer.Generation;

// Emits a user function with a trailing variadic Int parameter:
//   (define (vf_n [seed : Int] [parts : Int ...]) : Int seed)
//
// Variadic syntax `[parts : Int ...]` is parsed into AstBuilder's IsVariadic
// flag and lowered to `params int[]` in C# / IL. The body deliberately ignores
// `parts` — call-site arity (the count of trailing Int args) is what exercises
// the variadic-call codegen path, not the body's use of the param.
//
// Registered as a UserFunc with IsVariadic=true so ExprGenerator.GenCall knows
// to expand the last ParamType slot into 0-N Int args.
public sealed class VariadicFuncGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public VariadicFuncGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public UserFunc Generate(string name)
    {
        // Two shapes: variadic-only, and fixed-then-variadic. Both exercise the
        // params-codegen path; the fixed-prefix shape additionally exercises the
        // mixed-arity binding logic.
        var hasFixed = _ctx.Rng.NextDouble() < 0.5;

        if (hasFixed)
        {
            var seed = _ctx.Fresh();
            var parts = _ctx.Fresh();
            var def = $"(define ({name} [{seed} : Int] [{parts} : Int ...]) : Int\n  {seed})";
            return new UserFunc(
                name,
                UserFuncKind.Regular,
                [ExprType.Int, ExprType.Int],
                def,
                new HashSet<ExprType> { ExprType.Int },
                [false, false],
                false,
                IsVariadic: true
            );
        }
        else
        {
            var parts = _ctx.Fresh();
            // Body returns a constant rather than touching `parts` — the variadic
            // call-site path is exercised regardless.
            var def = $"(define ({name} [{parts} : Int ...]) : Int\n  0)";
            return new UserFunc(
                name,
                UserFuncKind.Regular,
                [ExprType.Int],
                def,
                new HashSet<ExprType> { ExprType.Int },
                [false],
                false,
                IsVariadic: true
            );
        }
    }
}
