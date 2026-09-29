namespace ZScheme.Fuzzer.Generation;

// Emits an `(interface IName (M [params...] : RetType) ...)` declaration, and —
// when earlier interfaces exist — occasionally an inheriting one:
//
//   (interface IChild : IBase (M ...))
//
// An inheriting interface re-declares none of the base's methods; implementers
// must supply both sets (the class and object generators expand the chain via
// ClassExprGenerator.CollectChainMethods), and inherited members gain their own
// `IChild-InheritedM` accessors. That exercises the transitive interface walk —
// subtyping through the chain, accessor registration for inherited methods, and
// both backends' resolution of a method found on a base.
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
        // ~40%: extend an earlier interface (interfaces are generated in index
        // order, so any already-registered one is a legal base). The base's own
        // base list comes along transitively in the emitted header.
        string? baseName = null;
        if (_ctx.UserInterfaces.Count > 0 && _ctx.Rng.NextDouble() < 0.40)
            baseName = _ctx.UserInterfaces[_ctx.Rng.Next(_ctx.UserInterfaces.Count)].Name;

        var name = _ctx.MangleTypeName($"IFuz_{index}");
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

        var baseMarker = baseName is null ? string.Empty : $" : {baseName}";
        var def = $"(interface {name}{baseMarker}\n{string.Join("\n", sigs)})";
        return new UserInterfaceDecl(name, methods, def, baseName is null ? [] : [baseName]);
    }
}
