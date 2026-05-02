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

        // Equality operators: forall a. (a, a) -> Bool
        var eqVar1 = new ZType.ZTypeVar(9220);
        env.Define("=", new ZType.ZForAllType([eqVar1.Id],
            new ZType.ZFuncType([eqVar1, eqVar1], ZType.Bool)));
        var eqVar2 = new ZType.ZTypeVar(9221);
        env.Define("!=", new ZType.ZForAllType([eqVar2.Id],
            new ZType.ZFuncType([eqVar2, eqVar2], ZType.Bool)));

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
        env.Define("string->int", new ZType.ZFuncType([ZType.String], ZType.Int));
        env.Define("double->float", new ZType.ZFuncType([ZType.Double], ZType.Float));
        env.Define("float->double", new ZType.ZFuncType([ZType.Float], ZType.Double));

        // The 6 collection conversion functions (mutable-array->array, array->mutable-array,
        // mutable-list->list, list->mutable-list, mutable-map->map, map->mutable-map) live in
        // stdlib (see packages/stdlib/src/{array,list,map,mutable/{array,list,map}}.zs).

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

    public IEnumerable<ZType> AllBoundTypes()
    {
        foreach (var t in _bindings.Values) yield return t;
        if (parent is not null)
            foreach (var t in parent.AllBoundTypes()) yield return t;
    }
}
