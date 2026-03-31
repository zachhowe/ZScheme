namespace ZScheme.Compiler.Types;

public sealed record BuiltinCtorInfo(string RuntimeType, string? CaseName);

public sealed class TypeEnv(TypeEnv? parent = null)
{
    private readonly Dictionary<string, ZType> _bindings = new();
    private readonly Dictionary<string, BuiltinCtorInfo> _builtinCtors = new();

    public static TypeEnv CreateRoot()
    {
        var env = new TypeEnv();

        // Arithmetic operators: forall a:{Int,Float}. (a, a) -> a
        IReadOnlySet<PrimitiveKind> numericKinds = new HashSet<PrimitiveKind>
            { PrimitiveKind.Int, PrimitiveKind.Float };
        var arithOps = new[] { "+", "-", "*", "/" };
        for (var i = 0; i < arithOps.Length; i++)
        {
            var numVar = new ZType.ZConstrainedVar(9200 + i, numericKinds);
            env.Define(arithOps[i], new ZType.ZForAllType([numVar.Id],
                new ZType.ZFuncType([numVar, numVar], numVar)));
        }

        // Modulo: (Int, Int) -> Int
        env.Define("%", new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int));

        // Ordered comparison operators: forall a:{Int,Float}. (a, a) -> Bool
        var ordOps = new[] { "<", ">", "<=", ">=" };
        for (var i = 0; i < ordOps.Length; i++)
        {
            var cmpVar = new ZType.ZConstrainedVar(9210 + i, numericKinds);
            env.Define(ordOps[i], new ZType.ZForAllType([cmpVar.Id],
                new ZType.ZFuncType([cmpVar, cmpVar], ZType.Bool)));
        }

        // Equality operators: (Int, Int) -> Bool (for now)
        var intCmpOp = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Bool);
        env.Define("=", intCmpOp);
        env.Define("!=", intCmpOp);

        // Boolean operators
        var boolBinOp = new ZType.ZFuncType([ZType.Bool, ZType.Bool], ZType.Bool);
        env.Define("and", boolBinOp);
        env.Define("or", boolBinOp);
        env.Define("not", new ZType.ZFuncType([ZType.Bool], ZType.Bool));

        // String concatenation
        env.Define("string-append", new ZType.ZFuncType([ZType.String, ZType.String], ZType.String));

        // Conversion functions
        env.Define("int->float", new ZType.ZFuncType([ZType.Int], ZType.Float));
        env.Define("float->int", new ZType.ZFuncType([ZType.Float], ZType.Int));
        env.Define("int->string", new ZType.ZFuncType([ZType.Int], ZType.String));

        // Mutable-Array <-> Array conversions
        var maArrA = new ZType.ZTypeVar(9300);
        env.Define("mutable-array->array",
            new ZType.ZForAllType([maArrA.Id],
                new ZType.ZFuncType(
                    [new ZType.ZNamedType("Mutable-Array", [maArrA])],
                    new ZType.ZNamedType("Array", [maArrA]))));

        var arrMaA = new ZType.ZTypeVar(9301);
        env.Define("array->mutable-array",
            new ZType.ZForAllType([arrMaA.Id],
                new ZType.ZFuncType(
                    [new ZType.ZNamedType("Array", [arrMaA])],
                    new ZType.ZNamedType("Mutable-Array", [arrMaA]))));

        // Mutable-List <-> List conversions
        var mlListA = new ZType.ZTypeVar(9302);
        env.Define("mutable-list->list",
            new ZType.ZForAllType([mlListA.Id],
                new ZType.ZFuncType(
                    [new ZType.ZNamedType("Mutable-List", [mlListA])],
                    new ZType.ZNamedType("List", [mlListA]))));

        var listMlA = new ZType.ZTypeVar(9303);
        env.Define("list->mutable-list",
            new ZType.ZForAllType([listMlA.Id],
                new ZType.ZFuncType(
                    [new ZType.ZNamedType("List", [listMlA])],
                    new ZType.ZNamedType("Mutable-List", [listMlA]))));

        // Mutable-Map <-> Map conversions
        var mmMapK = new ZType.ZTypeVar(9304);
        var mmMapV = new ZType.ZTypeVar(9305);
        env.Define("mutable-map->map",
            new ZType.ZForAllType([mmMapK.Id, mmMapV.Id],
                new ZType.ZFuncType(
                    [new ZType.ZNamedType("Mutable-Map", [mmMapK, mmMapV])],
                    new ZType.ZNamedType("Map", [mmMapK, mmMapV]))));

        var mapMmK = new ZType.ZTypeVar(9306);
        var mapMmV = new ZType.ZTypeVar(9307);
        env.Define("map->mutable-map",
            new ZType.ZForAllType([mapMmK.Id, mapMmV.Id],
                new ZType.ZFuncType(
                    [new ZType.ZNamedType("Map", [mapMmK, mapMmV])],
                    new ZType.ZNamedType("Mutable-Map", [mapMmK, mapMmV]))));

        return env;
    }

    public TypeEnv CreateChild()
    {
        return new TypeEnv(this);
    }

    public void Define(string name, ZType type)
    {
        _bindings[name] = type;
    }

    public ZType? Lookup(string name)
    {
        if (_bindings.TryGetValue(name, out var type))
            return type;
        return parent?.Lookup(name);
    }

    public bool Contains(string name)
    {
        return Lookup(name) is not null;
    }

    public void DefineBuiltinCtor(string name, BuiltinCtorInfo info)
    {
        _builtinCtors[name] = info;
    }

    public BuiltinCtorInfo? LookupBuiltinCtor(string name)
    {
        if (_builtinCtors.TryGetValue(name, out var info)) return info;
        return parent?.LookupBuiltinCtor(name);
    }
}
