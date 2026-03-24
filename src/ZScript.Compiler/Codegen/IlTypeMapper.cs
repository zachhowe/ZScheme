using System.Collections.Immutable;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Codegen;

/// <summary>
///     Maps ZScript types to CLR System.Type instances.
/// </summary>
public static class IlTypeMapper
{
    public static Type MapReturnTypeToClr(ZType type)
    {
        return type == ZType.Unit ? typeof(void) : MapToClr(type);
    }

    public static Type MapReturnTypeToClr(ZType type, IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap = null,
        IReadOnlyDictionary<int, Type>? typeVarMap = null)
    {
        return type == ZType.Unit ? typeof(void) : MapToClr(type, userTypes, typeParamMap, typeVarMap);
    }

    public static Type MapToClr(ZType type)
    {
        return type switch
        {
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Int } => typeof(int),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Long } => typeof(long),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Float } => typeof(float),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Double } => typeof(double),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Byte } => typeof(byte),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Char } => typeof(char),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Bool } => typeof(bool),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.String } => typeof(string),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } => typeof(ValueTuple),
            ZType.ZNamedType { Name: "List", TypeArgs: [var listT] } =>
                typeof(ImmutableList<>).MakeGenericType(MapToClr(listT)),
            ZType.ZNamedType { Name: "Vector", TypeArgs: [var vecT] } =>
                typeof(ImmutableArray<>).MakeGenericType(MapToClr(vecT)),
            ZType.ZNamedType { Name: "Map", TypeArgs: [var mapK, var mapV] } =>
                typeof(ImmutableDictionary<,>).MakeGenericType(MapToClr(mapK), MapToClr(mapV)),
            ZType.ZNamedType { Name: "Task", TypeArgs: [] } =>
                typeof(Task),
            ZType.ZNamedType { Name: "Task", TypeArgs: [var t] } =>
                typeof(Task<>).MakeGenericType(MapToClr(t)),
            ZType.ZFuncType ft => MakeFuncType(ft),
            _ => typeof(object)
        };
    }

    public static Type MapToClr(ZType type, IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap = null,
        IReadOnlyDictionary<int, Type>? typeVarMap = null)
    {
        return type switch
        {
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Int } => typeof(int),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Long } => typeof(long),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Float } => typeof(float),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Double } => typeof(double),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Byte } => typeof(byte),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Char } => typeof(char),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Bool } => typeof(bool),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.String } => typeof(string),
            ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit } => typeof(ValueTuple),
            ZType.ZTypeVar tv when typeVarMap is not null && typeVarMap.TryGetValue(tv.Id, out var gp) => gp,
            ZType.ZConstrainedVar cv when typeVarMap is not null && typeVarMap.TryGetValue(cv.Id, out var cgp) => cgp,
            ZType.ZNamedType { TypeArgs: [] } nt
                when typeParamMap is not null && typeParamMap.TryGetValue(nt.Name, out var tp) => tp,
            ZType.ZNamedType { Name: "List", TypeArgs: [var listT] } =>
                typeof(ImmutableList<>).MakeGenericType(MapToClr(listT, userTypes, typeParamMap, typeVarMap)),
            ZType.ZNamedType { Name: "Vector", TypeArgs: [var vecT] } =>
                typeof(ImmutableArray<>).MakeGenericType(MapToClr(vecT, userTypes, typeParamMap, typeVarMap)),
            ZType.ZNamedType { Name: "Map", TypeArgs: [var mapK, var mapV] } =>
                typeof(ImmutableDictionary<,>).MakeGenericType(
                    MapToClr(mapK, userTypes, typeParamMap, typeVarMap),
                    MapToClr(mapV, userTypes, typeParamMap, typeVarMap)),
            ZType.ZNamedType { Name: "Task", TypeArgs: [] } =>
                typeof(Task),
            ZType.ZNamedType { Name: "Task", TypeArgs: [var t] } =>
                typeof(Task<>).MakeGenericType(MapToClr(t, userTypes, typeParamMap, typeVarMap)),
            ZType.ZNamedType nt when userTypes.TryGetValue(nt.Name, out var ut) =>
                nt.TypeArgs.Count > 0 && ut.IsGenericTypeDefinition
                    ? ut.MakeGenericType(nt.TypeArgs.Select(a => MapToClr(a, userTypes, typeParamMap, typeVarMap))
                        .ToArray())
                    : ut,
            ZType.ZFuncType ft => MakeFuncType(ft, userTypes, typeParamMap, typeVarMap),
            _ => typeof(object)
        };
    }

    private static Type MakeFuncType(ZType.ZFuncType ft)
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft.Params.Select(MapToClr).ToArray();
            return paramTypes.Length switch
            {
                0 => typeof(Action),
                1 => typeof(Action<>).MakeGenericType(paramTypes),
                2 => typeof(Action<,>).MakeGenericType(paramTypes),
                3 => typeof(Action<,,>).MakeGenericType(paramTypes),
                4 => typeof(Action<,,,>).MakeGenericType(paramTypes),
                _ => typeof(object)
            };
        }

        var types = ft.Params.Select(MapToClr).Append(MapToClr(ft.Return)).ToArray();
        return types.Length switch
        {
            1 => typeof(Func<>).MakeGenericType(types),
            2 => typeof(Func<,>).MakeGenericType(types),
            3 => typeof(Func<,,>).MakeGenericType(types),
            4 => typeof(Func<,,,>).MakeGenericType(types),
            5 => typeof(Func<,,,,>).MakeGenericType(types),
            _ => typeof(object)
        };
    }

    private static Type MakeFuncType(ZType.ZFuncType ft, IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap,
        IReadOnlyDictionary<int, Type>? typeVarMap = null)
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft.Params.Select(p => MapToClr(p, userTypes, typeParamMap, typeVarMap)).ToArray();
            return paramTypes.Length switch
            {
                0 => typeof(Action),
                1 => typeof(Action<>).MakeGenericType(paramTypes),
                2 => typeof(Action<,>).MakeGenericType(paramTypes),
                3 => typeof(Action<,,>).MakeGenericType(paramTypes),
                4 => typeof(Action<,,,>).MakeGenericType(paramTypes),
                _ => typeof(object)
            };
        }

        var types = ft.Params.Select(p => MapToClr(p, userTypes, typeParamMap, typeVarMap))
            .Append(MapToClr(ft.Return, userTypes, typeParamMap, typeVarMap)).ToArray();
        return types.Length switch
        {
            1 => typeof(Func<>).MakeGenericType(types),
            2 => typeof(Func<,>).MakeGenericType(types),
            3 => typeof(Func<,,>).MakeGenericType(types),
            4 => typeof(Func<,,,>).MakeGenericType(types),
            5 => typeof(Func<,,,,>).MakeGenericType(types),
            _ => typeof(object)
        };
    }
}
