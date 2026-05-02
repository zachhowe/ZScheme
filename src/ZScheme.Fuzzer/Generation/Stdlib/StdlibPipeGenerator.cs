namespace ZScheme.Fuzzer.Generation.Stdlib;

// Generates `(|> x f1 f2 ...)` chains. The `|>` macro rewrites left-to-right
// into nested function applications.
//
// IMPORTANT: the macro's syntax-rules treats any list-shaped pipe operand as
// a partial application — `(|> x (f a) ...)` rewrites to `(f x a)`. That
// means an inline `(fn ...)` lambda in the chain is misinterpreted as a
// function call rather than a function value. To exercise the macro safely
// the generator binds each lambda to a name with `let*` first, then references
// the names in the pipe chain so each operand is a bare identifier and falls
// into the macro's `(|> x f rest ...)` case.
public sealed class StdlibPipeGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StdlibPipeGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    public bool IsImported() => _ctx.Imports.Contains(StdlibImport.Pipe);

    public string PipeChainToInt(Scope scope, int depth)
    {
        var seed = _exprs.GenInt(scope, depth - 1);
        var numStages = 2 + _ctx.Rng.Next(3); // 2..4 stages

        var bindings = new List<string>(numStages);
        var names = new List<string>(numStages);
        for (var i = 0; i < numStages; i++)
        {
            var name = _ctx.Fresh();
            names.Add(name);
            bindings.Add($"[{name} {BuildIntFnLambda(scope, depth - 1)}]");
        }

        var pipe = $"(|> {seed} {string.Join(" ", names)})";
        return $"(let* ({string.Join(" ", bindings)}) {pipe})";
    }

    private string BuildIntFnLambda(Scope scope, int depth)
    {
        var pname = _ctx.Fresh();
        var bodyScope = scope.Extend(pname, ExprType.Int);
        var bodyDepth = Math.Max(1, depth);
        var body = _exprs.GenInt(bodyScope, bodyDepth);
        return $"(fn [[{pname} : Int]] {body})";
    }
}
