using System.Collections.Immutable;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Codegen;

/// <summary>
///     Maps ZScript types to AsmResolver TypeSignature instances.
/// </summary>
public static class AsmResolverTypeMapper
{
    public static TypeSignature MapReturnTypeToClr(ZType type, ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes = null,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap = null,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap = null)
    {
        return type == ZType.Unit
            ? module.CorLibTypeFactory.Void
            : MapToClr(type, module, unitType, userTypes, typeParamMap, typeVarMap);
    }

    public static TypeSignature MapToClr(ZType type, ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes = null,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap = null,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap = null)
    {
        return type switch
        {
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Int } => module.CorLibTypeFactory.Int32,
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Long } => module.CorLibTypeFactory.Int64,
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Float } => module.CorLibTypeFactory.Single,
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Double } => module.CorLibTypeFactory.Double,
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Byte } => module.CorLibTypeFactory.Byte,
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Char } => module.CorLibTypeFactory.Char,
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Bool } => module.CorLibTypeFactory.Boolean,
            ZType.ZPrimitiveType { Kind: PrimitiveKind.String } => module.CorLibTypeFactory.String,
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } => unitType,
            ZType.ZTypeVar tv when typeVarMap is not null && typeVarMap.TryGetValue(tv.Id, out var gp) => gp,
            ZType.ZConstrainedVar cv when typeVarMap is not null && typeVarMap.TryGetValue(cv.Id, out var cgp) => cgp,
            ZType.ZNamedType { TypeArgs: [] } nt
                when typeParamMap is not null && typeParamMap.TryGetValue(nt.Name, out var tp) => tp,
            ZType.ZNamedType { Name: "List", TypeArgs: [var listT] } =>
                MakeGenericInstance(module, typeof(ImmutableList<>),
                    [MapToClr(listT, module, unitType, userTypes, typeParamMap, typeVarMap)]),
            ZType.ZNamedType { Name: "Array", TypeArgs: [var vecT] } =>
                MakeGenericInstance(module, typeof(ImmutableArray<>),
                    [MapToClr(vecT, module, unitType, userTypes, typeParamMap, typeVarMap)]),
            ZType.ZNamedType { Name: "Mutable-Array", TypeArgs: [var arrT] } =>
                new SzArrayTypeSignature(MapToClr(arrT, module, unitType, userTypes, typeParamMap, typeVarMap)),
            ZType.ZNamedType { Name: "Mutable-List", TypeArgs: [var mlT] } =>
                MakeGenericInstance(module, typeof(List<>),
                    [MapToClr(mlT, module, unitType, userTypes, typeParamMap, typeVarMap)]),
            ZType.ZNamedType { Name: "Map", TypeArgs: [var mapK, var mapV] } =>
                MakeGenericInstance(module, typeof(ImmutableDictionary<,>),
                [
                    MapToClr(mapK, module, unitType, userTypes, typeParamMap, typeVarMap),
                    MapToClr(mapV, module, unitType, userTypes, typeParamMap, typeVarMap)
                ]),
            ZType.ZNamedType { Name: "Mutable-Map", TypeArgs: [var mmK, var mmV] } =>
                MakeGenericInstance(module, typeof(Dictionary<,>),
                [
                    MapToClr(mmK, module, unitType, userTypes, typeParamMap, typeVarMap),
                    MapToClr(mmV, module, unitType, userTypes, typeParamMap, typeVarMap)
                ]),
            ZType.ZNamedType { Name: "Task", TypeArgs: [] } =>
                ImportTypeCorLibAware(module, typeof(Task)).ToTypeSignature(false),
            ZType.ZNamedType { Name: "Task", TypeArgs: [var t] } =>
                MakeGenericInstance(module, typeof(Task<>),
                    [MapToClr(t, module, unitType, userTypes, typeParamMap, typeVarMap)]),
            ZType.ZNamedType nt when userTypes is not null && userTypes.TryGetValue(nt.Name, out var ut) =>
                nt.TypeArgs.Count > 0
                    ? ut.ToTypeDefOrRef().ToTypeSignature(false)
                        .MakeGenericInstanceType(false, nt.TypeArgs
                            .Select(ta => MapToClr(ta, module, unitType, userTypes, typeParamMap, typeVarMap))
                            .ToArray())
                    : ut,
            ZType.ZFuncType ft => MakeFuncType(ft, module, unitType, userTypes, typeParamMap, typeVarMap),
            _ => module.CorLibTypeFactory.Object
        };
    }

    private static GenericInstanceTypeSignature MakeGenericInstance(ModuleDefinition module, Type openClrType,
        TypeSignature[] typeArgs)
    {
        var imported = ImportTypeCorLibAware(module, openClrType);
        return imported.ToTypeSignature(openClrType.IsValueType).MakeGenericInstanceType(openClrType.IsValueType, typeArgs);
    }

    /// <summary>
    ///     Imports a CLR type, routing corlib types (Func, Action, Task, etc.) through the
    ///     module's configured corlib scope instead of System.Private.CoreLib.
    /// </summary>
    internal static ITypeDefOrRef ImportTypeCorLibAware(ModuleDefinition module, Type clrType)
    {
        var imported = (ITypeDefOrRef)module.DefaultImporter.ImportType(clrType);
        var asmName = clrType.Assembly.GetName().Name;
        if (asmName is "System.Private.CoreLib" or "mscorlib" && imported is TypeReference tr)
            tr.Scope = module.CorLibTypeFactory.CorLibScope;
        return imported;
    }

    private static TypeSignature MakeFuncType(ZType.ZFuncType ft, ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap)
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft.Params.Select(p => MapToClr(p, module, unitType, userTypes, typeParamMap, typeVarMap))
                .ToArray();
            if (paramTypes.Length == 0)
                return ImportTypeCorLibAware(module, typeof(Action)).ToTypeSignature(false);
            var actionOpenType = paramTypes.Length switch
            {
                1 => typeof(Action<>),
                2 => typeof(Action<,>),
                3 => typeof(Action<,,>),
                4 => typeof(Action<,,,>),
                _ => typeof(object)
            };
            if (actionOpenType == typeof(object))
                return module.CorLibTypeFactory.Object;
            return MakeGenericInstance(module, actionOpenType, paramTypes);
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
            return module.CorLibTypeFactory.Object;
        return MakeGenericInstance(module, funcOpenType, types);
    }
}
