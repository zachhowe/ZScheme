namespace ZScript.Compiler.Codegen;

using System.Collections.Immutable;
using Mono.Cecil;
using ZScript.Compiler.Types;

/// <summary>
/// Maps ZScript types to Mono.Cecil TypeReference instances.
/// </summary>
public static class CecilTypeMapper
{
    public static TypeReference MapReturnTypeToClr(ZType type, ModuleDefinition module,
        TypeReference unitType,
        IReadOnlyDictionary<string, TypeReference>? userTypes = null,
        IReadOnlyDictionary<string, TypeReference>? typeParamMap = null,
        IReadOnlyDictionary<int, TypeReference>? typeVarMap = null) =>
        type == ZType.Unit
            ? module.TypeSystem.Void
            : MapToClr(type, module, unitType, userTypes, typeParamMap, typeVarMap);

    public static TypeReference MapToClr(ZType type, ModuleDefinition module,
        TypeReference unitType,
        IReadOnlyDictionary<string, TypeReference>? userTypes = null,
        IReadOnlyDictionary<string, TypeReference>? typeParamMap = null,
        IReadOnlyDictionary<int, TypeReference>? typeVarMap = null) => type switch
    {
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Int } => module.TypeSystem.Int32,
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Long } => module.TypeSystem.Int64,
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Float } => module.TypeSystem.Single,
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Double } => module.TypeSystem.Double,
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Byte } => module.TypeSystem.Byte,
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Char } => module.TypeSystem.Char,
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Bool } => module.TypeSystem.Boolean,
        ZType.ZPrimitiveType { Kind: PrimitiveKind.String } => module.TypeSystem.String,
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } => unitType,
        ZType.ZTypeVar tv when typeVarMap is not null && typeVarMap.TryGetValue(tv.Id, out var gp) => gp,
        ZType.ZConstrainedVar cv when typeVarMap is not null && typeVarMap.TryGetValue(cv.Id, out var cgp) => cgp,
        ZType.ZNamedType { TypeArgs: [] } nt
            when typeParamMap is not null && typeParamMap.TryGetValue(nt.Name, out var tp) => tp,
        ZType.ZNamedType { Name: "List", TypeArgs: [var listT] } =>
            MakeGenericInstance(module.ImportReference(typeof(ImmutableList<>)), module,
                [MapToClr(listT, module, unitType, userTypes, typeParamMap, typeVarMap)]),
        ZType.ZNamedType { Name: "Vector", TypeArgs: [var vecT] } =>
            MakeGenericInstance(module.ImportReference(typeof(ImmutableArray<>)), module,
                [MapToClr(vecT, module, unitType, userTypes, typeParamMap, typeVarMap)]),
        ZType.ZNamedType { Name: "Map", TypeArgs: [var mapK, var mapV] } =>
            MakeGenericInstance(module.ImportReference(typeof(ImmutableDictionary<,>)), module,
                [MapToClr(mapK, module, unitType, userTypes, typeParamMap, typeVarMap),
                 MapToClr(mapV, module, unitType, userTypes, typeParamMap, typeVarMap)]),
        ZType.ZNamedType { Name: "Task", TypeArgs: [] } =>
            module.ImportReference(typeof(System.Threading.Tasks.Task)),
        ZType.ZNamedType { Name: "Task", TypeArgs: [var t] } =>
            MakeGenericInstance(module.ImportReference(typeof(System.Threading.Tasks.Task<>)), module,
                [MapToClr(t, module, unitType, userTypes, typeParamMap, typeVarMap)]),
        ZType.ZNamedType nt when userTypes is not null && userTypes.TryGetValue(nt.Name, out var ut) =>
            nt.TypeArgs.Count > 0 && ut.HasGenericParameters
                ? MakeGenericInstance(ut, module,
                    nt.TypeArgs.Select(a => MapToClr(a, module, unitType, userTypes, typeParamMap, typeVarMap)).ToArray())
                : ut,
        ZType.ZFuncType ft => MakeFuncType(ft, module, unitType, userTypes, typeParamMap, typeVarMap),
        _ => module.TypeSystem.Object
    };

    private static GenericInstanceType MakeGenericInstance(TypeReference openType, ModuleDefinition module,
        TypeReference[] typeArgs)
    {
        var git = new GenericInstanceType(openType);
        foreach (var arg in typeArgs)
            git.GenericArguments.Add(arg);
        return git;
    }

    private static TypeReference MakeFuncType(ZType.ZFuncType ft, ModuleDefinition module,
        TypeReference unitType,
        IReadOnlyDictionary<string, TypeReference>? userTypes,
        IReadOnlyDictionary<string, TypeReference>? typeParamMap,
        IReadOnlyDictionary<int, TypeReference>? typeVarMap)
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft.Params.Select(p => MapToClr(p, module, unitType, userTypes, typeParamMap, typeVarMap)).ToArray();
            if (paramTypes.Length == 0)
                return module.ImportReference(typeof(Action));
            var actionOpenType = paramTypes.Length switch
            {
                1 => typeof(Action<>),
                2 => typeof(Action<,>),
                3 => typeof(Action<,,>),
                4 => typeof(Action<,,,>),
                _ => typeof(object)
            };
            if (actionOpenType == typeof(object))
                return module.TypeSystem.Object;
            return MakeGenericInstance(module.ImportReference(actionOpenType), module, paramTypes);
        }

        var types = ft.Params.Select(p => MapToClr(p, module, unitType, userTypes, typeParamMap, typeVarMap))
            .Append(MapToClr(ft.Return, module, unitType, userTypes, typeParamMap, typeVarMap)).ToArray();
        var funcOpenType = types.Length switch
        {
            1 => typeof(Func<>),
            2 => typeof(Func<,>),
            3 => typeof(Func<,,>),
            4 => typeof(Func<,,,>),
            5 => typeof(Func<,,,,>),
            _ => typeof(object)
        };
        if (funcOpenType == typeof(object))
            return module.TypeSystem.Object;
        return MakeGenericInstance(module.ImportReference(funcOpenType), module, types);
    }
}
