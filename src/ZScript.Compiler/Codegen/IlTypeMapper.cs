namespace ZScript.Compiler.Codegen;

using System.Collections.Immutable;
using ZScript.Compiler.Types;
using ZScript.Runtime;

/// <summary>
/// Maps ZScript types to CLR System.Type instances.
/// </summary>
public static class IlTypeMapper
{
    public static Type MapReturnTypeToClr(ZType type) =>
        type == ZType.Unit ? typeof(void) : MapToClr(type);

    public static Type MapReturnTypeToClr(ZType type, IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap = null) =>
        type == ZType.Unit ? typeof(void) : MapToClr(type, userTypes, typeParamMap);

    public static Type MapToClr(ZType type) => type switch
    {
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Int } => typeof(int),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Long } => typeof(long),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Float } => typeof(float),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Double } => typeof(double),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Byte } => typeof(byte),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Char } => typeof(char),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Bool } => typeof(bool),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.String } => typeof(string),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } => typeof(ZsUnit),
        ZType.ZNamedType { Name: "List", TypeArgs: [var listT] } =>
            typeof(ImmutableList<>).MakeGenericType(MapToClr(listT)),
        ZType.ZNamedType { Name: "Vector", TypeArgs: [var vecT] } =>
            typeof(ImmutableArray<>).MakeGenericType(MapToClr(vecT)),
        ZType.ZNamedType { Name: "Map", TypeArgs: [var mapK, var mapV] } =>
            typeof(ImmutableDictionary<,>).MakeGenericType(MapToClr(mapK), MapToClr(mapV)),
        ZType.ZNamedType { Name: "Task", TypeArgs: [] } =>
            typeof(System.Threading.Tasks.Task),
        ZType.ZNamedType { Name: "Task", TypeArgs: [var t] } =>
            typeof(System.Threading.Tasks.Task<>).MakeGenericType(MapToClr(t)),
        ZType.ZFuncType ft => MakeFuncType(ft),
        _ => typeof(object)
    };

    public static Type MapToClr(ZType type, IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap = null) => type switch
    {
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Int } => typeof(int),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Long } => typeof(long),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Float } => typeof(float),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Double } => typeof(double),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Byte } => typeof(byte),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Char } => typeof(char),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Bool } => typeof(bool),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.String } => typeof(string),
        ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } => typeof(ZsUnit),
        ZType.ZNamedType { TypeArgs: [] } nt
            when typeParamMap is not null && typeParamMap.TryGetValue(nt.Name, out var tp) => tp,
        ZType.ZNamedType { Name: "List", TypeArgs: [var listT] } =>
            typeof(ImmutableList<>).MakeGenericType(MapToClr(listT, userTypes, typeParamMap)),
        ZType.ZNamedType { Name: "Vector", TypeArgs: [var vecT] } =>
            typeof(ImmutableArray<>).MakeGenericType(MapToClr(vecT, userTypes, typeParamMap)),
        ZType.ZNamedType { Name: "Map", TypeArgs: [var mapK, var mapV] } =>
            typeof(ImmutableDictionary<,>).MakeGenericType(
                MapToClr(mapK, userTypes, typeParamMap), MapToClr(mapV, userTypes, typeParamMap)),
        ZType.ZNamedType { Name: "Task", TypeArgs: [] } =>
            typeof(System.Threading.Tasks.Task),
        ZType.ZNamedType { Name: "Task", TypeArgs: [var t] } =>
            typeof(System.Threading.Tasks.Task<>).MakeGenericType(MapToClr(t, userTypes, typeParamMap)),
        ZType.ZNamedType nt when userTypes.TryGetValue(nt.Name, out var ut) =>
            nt.TypeArgs.Count > 0 && ut.IsGenericTypeDefinition
                ? ut.MakeGenericType(nt.TypeArgs.Select(a => MapToClr(a, userTypes, typeParamMap)).ToArray())
                : ut,
        ZType.ZFuncType ft => MakeFuncType(ft, userTypes, typeParamMap),
        _ => typeof(object)
    };

    private static Type MakeFuncType(ZType.ZFuncType ft)
    {
        var types = ft.Params.Select(MapToClr).Append(MapToClr(ft.Return)).ToArray();
        return types.Length switch
        {
            1 => typeof(Func<>).MakeGenericType(types),
            2 => typeof(Func<,>).MakeGenericType(types),
            3 => typeof(Func<,,>).MakeGenericType(types),
            4 => typeof(Func<,,,>).MakeGenericType(types),
            5 => typeof(Func<,,,,>).MakeGenericType(types),
            _ => typeof(object) // fallback
        };
    }

    private static Type MakeFuncType(ZType.ZFuncType ft, IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap)
    {
        var types = ft.Params.Select(p => MapToClr(p, userTypes, typeParamMap))
            .Append(MapToClr(ft.Return, userTypes, typeParamMap)).ToArray();
        return types.Length switch
        {
            1 => typeof(Func<>).MakeGenericType(types),
            2 => typeof(Func<,>).MakeGenericType(types),
            3 => typeof(Func<,,>).MakeGenericType(types),
            4 => typeof(Func<,,,>).MakeGenericType(types),
            5 => typeof(Func<,,,,>).MakeGenericType(types),
            _ => typeof(object) // fallback
        };
    }
}
