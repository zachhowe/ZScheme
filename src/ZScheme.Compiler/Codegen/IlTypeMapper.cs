using System.Collections.Immutable;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Maps ZScheme types to CLR System.Type instances.
/// </summary>
public static class IlTypeMapper
{
    public static Type MapToClr(ZType type, DiagnosticBag? diagnostics = null)
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
                typeof(ImmutableList<>).MakeGenericType(MapToClr(listT, diagnostics)),
            ZType.ZNamedType { Name: "Array", TypeArgs: [var vecT] } =>
                typeof(ImmutableArray<>).MakeGenericType(MapToClr(vecT, diagnostics)),
            ZType.ZNamedType { Name: "Mutable-Array", TypeArgs: [var arrT] } =>
                MapToClr(arrT, diagnostics).MakeArrayType(),
            ZType.ZNamedType { Name: "Mutable-List", TypeArgs: [var mlT] } =>
                typeof(List<>).MakeGenericType(MapToClr(mlT, diagnostics)),
            ZType.ZNamedType { Name: "Pair", TypeArgs: [var pairK, var pairV] } =>
                typeof(KeyValuePair<,>).MakeGenericType(MapToClr(pairK, diagnostics), MapToClr(pairV, diagnostics)),
            ZType.ZNamedType { Name: "Map", TypeArgs: [var mapK, var mapV] } =>
                typeof(ImmutableDictionary<,>).MakeGenericType(MapToClr(mapK, diagnostics), MapToClr(mapV, diagnostics)),
            ZType.ZNamedType { Name: "Mutable-Map", TypeArgs: [var mmK, var mmV] } =>
                typeof(Dictionary<,>).MakeGenericType(MapToClr(mmK, diagnostics), MapToClr(mmV, diagnostics)),
            ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] } =>
                typeof(Task),
            ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [var t] } =>
                typeof(Task<>).MakeGenericType(MapToClr(t, diagnostics)),
            ZType.ZNamedType { Name: "ValueTuple" } vt when vt.TypeArgs.Count > 0 =>
                MakeValueTupleType(vt.TypeArgs.Select(a => MapToClr(a, diagnostics)).ToArray(), diagnostics),
            ZType.ZNullableType { Inner: var inner } =>
                MapToClr(inner, diagnostics) is { IsValueType: true } vt
                    ? typeof(Nullable<>).MakeGenericType(vt)
                    : MapToClr(inner, diagnostics),
            ZType.ZFuncType ft => MakeFuncType(ft, diagnostics),
            _ => WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: Cannot map type '{type}' to CLR type, falling back to object")
        };
    }

    private static Type MapToClr(ZType type, IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap = null,
        IReadOnlyDictionary<int, Type>? typeVarMap = null,
        DiagnosticBag? diagnostics = null)
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
                typeof(ImmutableList<>).MakeGenericType(MapToClr(listT, userTypes, typeParamMap, typeVarMap, diagnostics)),
            ZType.ZNamedType { Name: "Array", TypeArgs: [var vecT] } =>
                typeof(ImmutableArray<>).MakeGenericType(MapToClr(vecT, userTypes, typeParamMap, typeVarMap, diagnostics)),
            ZType.ZNamedType { Name: "Mutable-Array", TypeArgs: [var arrT] } =>
                MapToClr(arrT, userTypes, typeParamMap, typeVarMap, diagnostics).MakeArrayType(),
            ZType.ZNamedType { Name: "Mutable-List", TypeArgs: [var mlT] } =>
                typeof(List<>).MakeGenericType(MapToClr(mlT, userTypes, typeParamMap, typeVarMap, diagnostics)),
            ZType.ZNamedType { Name: "Map", TypeArgs: [var mapK, var mapV] } =>
                typeof(ImmutableDictionary<,>).MakeGenericType(
                    MapToClr(mapK, userTypes, typeParamMap, typeVarMap, diagnostics),
                    MapToClr(mapV, userTypes, typeParamMap, typeVarMap, diagnostics)),
            ZType.ZNamedType { Name: "Mutable-Map", TypeArgs: [var mmK, var mmV] } =>
                typeof(Dictionary<,>).MakeGenericType(
                    MapToClr(mmK, userTypes, typeParamMap, typeVarMap, diagnostics),
                    MapToClr(mmV, userTypes, typeParamMap, typeVarMap, diagnostics)),
            ZType.ZNamedType { Name: "Task", TypeArgs: [] } =>
                typeof(Task),
            ZType.ZNamedType { Name: "Task", TypeArgs: [var t] } =>
                typeof(Task<>).MakeGenericType(MapToClr(t, userTypes, typeParamMap, typeVarMap, diagnostics)),
            ZType.ZNamedType nt when userTypes.TryGetValue(nt.Name, out var ut) =>
                nt.TypeArgs.Count > 0 && ut.IsGenericTypeDefinition
                    ? ut.MakeGenericType(nt.TypeArgs.Select(a => MapToClr(a, userTypes, typeParamMap, typeVarMap, diagnostics))
                        .ToArray())
                    : ut,
            ZType.ZNamedType { Name: "ValueTuple" } vt when vt.TypeArgs.Count > 0 =>
                MakeValueTupleType(vt.TypeArgs.Select(t => MapToClr(t, userTypes, typeParamMap, typeVarMap, diagnostics)).ToArray(), diagnostics),
            ZType.ZNullableType { Inner: var inner } =>
                typeof(Nullable<>).MakeGenericType(MapToClr(inner, userTypes, typeParamMap, typeVarMap, diagnostics)),
            ZType.ZFuncType ft => MakeFuncType(ft, userTypes, typeParamMap, typeVarMap, diagnostics),
            _ => WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: Cannot map type '{type}' to CLR type, falling back to object")
        };
    }

    private static Type MakeFuncType(ZType.ZFuncType ft, DiagnosticBag? diagnostics)
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft.Params.Select(p => MapToClr(p, diagnostics)).ToArray();
            return paramTypes.Length switch
            {
                0 => typeof(Action),
                1 => typeof(Action<>).MakeGenericType(paramTypes),
                2 => typeof(Action<,>).MakeGenericType(paramTypes),
                3 => typeof(Action<,,>).MakeGenericType(paramTypes),
                4 => typeof(Action<,,,>).MakeGenericType(paramTypes),
                _ => WarnAndFallbackToObject(diagnostics,
                    $"IlTypeMapper: Action delegate with {paramTypes.Length} parameters exceeds maximum of 4, falling back to object")
            };
        }

        var types = ft.Params.Select(p => MapToClr(p, diagnostics)).Append(MapToClr(ft.Return, diagnostics)).ToArray();
        return types.Length switch
        {
            1 => typeof(Func<>).MakeGenericType(types),
            2 => typeof(Func<,>).MakeGenericType(types),
            3 => typeof(Func<,,>).MakeGenericType(types),
            4 => typeof(Func<,,,>).MakeGenericType(types),
            5 => typeof(Func<,,,,>).MakeGenericType(types),
            _ => WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: Func delegate with {types.Length} type arguments exceeds maximum of 5, falling back to object")
        };
    }

    private static Type MakeFuncType(ZType.ZFuncType ft, IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap,
        IReadOnlyDictionary<int, Type>? typeVarMap = null,
        DiagnosticBag? diagnostics = null)
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft.Params.Select(p => MapToClr(p, userTypes, typeParamMap, typeVarMap, diagnostics)).ToArray();
            return paramTypes.Length switch
            {
                0 => typeof(Action),
                1 => typeof(Action<>).MakeGenericType(paramTypes),
                2 => typeof(Action<,>).MakeGenericType(paramTypes),
                3 => typeof(Action<,,>).MakeGenericType(paramTypes),
                4 => typeof(Action<,,,>).MakeGenericType(paramTypes),
                _ => WarnAndFallbackToObject(diagnostics,
                    $"IlTypeMapper: Action delegate with {paramTypes.Length} parameters exceeds maximum of 4, falling back to object")
            };
        }

        var types = ft.Params.Select(p => MapToClr(p, userTypes, typeParamMap, typeVarMap, diagnostics))
            .Append(MapToClr(ft.Return, userTypes, typeParamMap, typeVarMap, diagnostics)).ToArray();
        return types.Length switch
        {
            1 => typeof(Func<>).MakeGenericType(types),
            2 => typeof(Func<,>).MakeGenericType(types),
            3 => typeof(Func<,,>).MakeGenericType(types),
            4 => typeof(Func<,,,>).MakeGenericType(types),
            5 => typeof(Func<,,,,>).MakeGenericType(types),
            _ => WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: Func delegate with {types.Length} type arguments exceeds maximum of 5, falling back to object")
        };
    }

    private static Type MakeValueTupleType(Type[] typeArgs, DiagnosticBag? diagnostics = null)
    {
        return typeArgs.Length switch
        {
            1 => typeof(ValueTuple<>).MakeGenericType(typeArgs),
            2 => typeof(ValueTuple<,>).MakeGenericType(typeArgs),
            3 => typeof(ValueTuple<,,>).MakeGenericType(typeArgs),
            4 => typeof(ValueTuple<,,,>).MakeGenericType(typeArgs),
            5 => typeof(ValueTuple<,,,,>).MakeGenericType(typeArgs),
            6 => typeof(ValueTuple<,,,,,>).MakeGenericType(typeArgs),
            7 => typeof(ValueTuple<,,,,,,>).MakeGenericType(typeArgs),
            _ => WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: ValueTuple with {typeArgs.Length} elements exceeds maximum of 7, falling back to object")
        };
    }

    private static Type WarnAndFallbackToObject(DiagnosticBag? diagnostics, string message)
    {
        diagnostics?.Warning(message, SourceSpan.None);
        return typeof(object);
    }
}
