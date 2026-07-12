namespace ZScheme.Fuzzer.Generation;

// Emits an `(define-interface IName (M [params...] : RetType) ...)` declaration.
//
// Method params and returns range over the ground types {Int, Bool, Float},
// Int-biased so most implementations stay on the well-trodden GenInt path while
// Bool/Float signatures exercise interface-dispatch codegen at other primitive
// widths. Interface generic parameters remain out of scope.
public sealed class InterfaceGenerator
{
    private readonly GeneratorContext _ctx;

    public InterfaceGenerator(GeneratorContext ctx)
    {
        _ctx = ctx;
    }

    private ExprType PickGround()
    {
        var roll = _ctx.Rng.NextDouble();
        if (roll < 0.65)
            return ExprType.Int;
        return roll < 0.825 ? ExprType.Bool : ExprType.Float;
    }

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
                var pt = PickGround();
                paramTypes.Add(pt);
                paramSigs.Add($"[p{p} : {ExprGenerator.TypeNameOf(pt)}]");
            }

            var retType = PickGround();
            methods.Add(new UserInterfaceMethod(methodName, paramTypes, retType));
            sigs.Add(
                $"  ({methodName} {string.Join(" ", paramSigs)} : {ExprGenerator.TypeNameOf(retType)})"
            );
        }

        var def = $"(define-interface {name}\n{string.Join("\n", sigs)})";
        return new UserInterfaceDecl(name, methods, def);
    }
}
