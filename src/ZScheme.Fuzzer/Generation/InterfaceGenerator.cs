namespace ZScheme.Fuzzer.Generation;

// Emits an `(interface IName (M [params...] : RetType) ...)` declaration.
//
// Methods are limited to Int params and Int return so implementing classes/objects
// can fill bodies with the existing ExprGenerator.GenInt path. Interface generic
// parameters and non-Int signatures are intentionally out of scope here — they are
// a separate body of work that needs matching support across UserClassDecl and
// ObjectExprGenerator.
public sealed class InterfaceGenerator
{
    private readonly GeneratorContext _ctx;

    public InterfaceGenerator(GeneratorContext ctx) { _ctx = ctx; }

    public UserInterfaceDecl GenerateInterface(int index)
    {
        var name = $"IFuz_{index}";
        var numMethods = 1 + _ctx.Rng.Next(3);
        var methods = new List<UserInterfaceMethod>(numMethods);
        var sigs = new List<string>(numMethods);

        for (var i = 0; i < numMethods; i++)
        {
            var methodName = $"M{index}_{i}";
            var arity = _ctx.Rng.Next(3); // 0..2 params
            var paramTypes = new List<ExprType>(arity);
            var paramSigs = new List<string>(arity);
            for (var p = 0; p < arity; p++)
            {
                paramTypes.Add(ExprType.Int);
                paramSigs.Add($"[p{p} : Int]");
            }
            methods.Add(new UserInterfaceMethod(methodName, paramTypes, ExprType.Int));
            sigs.Add($"  ({methodName} {string.Join(" ", paramSigs)} : Int)");
        }

        var def = $"(interface {name}\n{string.Join("\n", sigs)})";
        return new UserInterfaceDecl(name, methods, def);
    }
}
