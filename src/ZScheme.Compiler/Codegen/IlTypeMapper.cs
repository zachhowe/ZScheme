using System.Collections.Immutable;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Maps ZScheme types to CLR System.Type instances.
/// </summary>
public static class IlTypeMapper
{
    public static Type MapToClr(ZType type, DiagnosticBag? diagnostics = null,
        TypeAliasRegistry? typeAliases = null)
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
            ZType.ZNamedType nt when typeAliases is not null
                                     && typeAliases.TryGet(nt.Name, out var alias) && alias is not null =>
                ApplyAlias(alias, nt.TypeArgs, diagnostics, typeAliases),
            ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] } =>
                typeof(Task),
            ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [var t] } =>
                typeof(Task<>).MakeGenericType(MapToClr(t, diagnostics, typeAliases)),
            ZType.ZNamedType { Name: "ValueTuple" } vt when vt.TypeArgs.Count > 0 =>
                MakeValueTupleType(vt.TypeArgs.Select(a => MapToClr(a, diagnostics, typeAliases)).ToArray(), diagnostics),
            ZType.ZNullableType { Inner: var inner } =>
                MapToClr(inner, diagnostics, typeAliases) is { IsValueType: true } vt
                    ? typeof(Nullable<>).MakeGenericType(vt)
                    : MapToClr(inner, diagnostics, typeAliases),
            ZType.ZFuncType ft => MakeFuncType(ft, diagnostics, typeAliases),
            ZType.ZNamedType clrNt when clrNt.Name.Contains('.') =>
                ResolveClrNamedType(clrNt) ?? WarnAndFallbackToObject(diagnostics,
                    $"IlTypeMapper: Cannot map type '{type}' to CLR type, falling back to object"),
            _ => WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: Cannot map type '{type}' to CLR type, falling back to object")
        };
    }

    private static Type? ResolveClrNamedType(ZType.ZNamedType nt)
    {
        var clrType = Type.GetType(nt.Name) ?? Type.GetType($"{nt.Name}, System.Runtime");
        if (clrType is null)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                clrType = asm.GetType(nt.Name);
                if (clrType is not null) break;
            }
        return clrType;
    }

    private static Type? ResolveAliasTarget(TypeAliasInfo alias)
    {
        var arity = alias.TypeParams.Count;
        var openName = arity > 0 ? $"{alias.ClrTarget}`{arity}" : alias.ClrTarget;
        if (alias.AssemblyHint is not null)
        {
            var hinted = Type.GetType($"{openName}, {alias.AssemblyHint}");
            if (hinted is not null) return hinted;
        }
        var direct = Type.GetType(openName) ?? Type.GetType($"{openName}, System.Runtime");
        if (direct is not null) return direct;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(openName);
            if (t is not null) return t;
        }
        return null;
    }

    private static Type ApplyAlias(TypeAliasInfo alias, IReadOnlyList<ZType> typeArgs,
        DiagnosticBag? diagnostics, TypeAliasRegistry? typeAliases)
    {
        if (typeArgs.Count != alias.TypeParams.Count)
            return WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: Alias '{alias.Name}' expects {alias.TypeParams.Count} type args, got {typeArgs.Count}");
        var mapped = typeArgs.Select(a => MapToClr(a, diagnostics, typeAliases)).ToArray();
        if (alias.Kind == TypeAliasKind.SzArray)
            return mapped[0].MakeArrayType();
        var openType = ResolveAliasTarget(alias);
        if (openType is null)
            return WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: Cannot resolve CLR type for alias '{alias.Name}' -> '{alias.ClrTarget}'");
        return mapped.Length == 0 ? openType : openType.MakeGenericType(mapped);
    }

    private static Type ApplyAlias(TypeAliasInfo alias, IReadOnlyList<ZType> typeArgs,
        IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap,
        IReadOnlyDictionary<int, Type>? typeVarMap,
        DiagnosticBag? diagnostics, TypeAliasRegistry? typeAliases)
    {
        if (typeArgs.Count != alias.TypeParams.Count)
            return WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: Alias '{alias.Name}' expects {alias.TypeParams.Count} type args, got {typeArgs.Count}");
        var mapped = typeArgs
            .Select(a => MapToClr(a, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases))
            .ToArray();
        if (alias.Kind == TypeAliasKind.SzArray)
            return mapped[0].MakeArrayType();
        var openType = ResolveAliasTarget(alias);
        if (openType is null)
            return WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: Cannot resolve CLR type for alias '{alias.Name}' -> '{alias.ClrTarget}'");
        return mapped.Length == 0 ? openType : openType.MakeGenericType(mapped);
    }

    public static Type MapToClr(ZType type, IReadOnlyDictionary<string, Type> userTypes,
        IReadOnlyDictionary<string, Type>? typeParamMap = null,
        IReadOnlyDictionary<int, Type>? typeVarMap = null,
        DiagnosticBag? diagnostics = null,
        TypeAliasRegistry? typeAliases = null)
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
            ZType.ZNamedType nt when typeAliases is not null
                                     && typeAliases.TryGet(nt.Name, out var alias) && alias is not null =>
                ApplyAlias(alias, nt.TypeArgs, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases),
            ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] } =>
                typeof(Task),
            ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [var t] } =>
                typeof(Task<>).MakeGenericType(MapToClr(t, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases)),
            ZType.ZNamedType nt when userTypes.TryGetValue(nt.Name, out var ut) =>
                nt.TypeArgs.Count > 0 && ut.IsGenericTypeDefinition
                    ? ut.MakeGenericType(nt.TypeArgs
                        .Select(a => MapToClr(a, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases))
                        .ToArray())
                    : ut,
            ZType.ZNamedType { Name: "ValueTuple" } vt when vt.TypeArgs.Count > 0 =>
                MakeValueTupleType(
                    vt.TypeArgs.Select(t => MapToClr(t, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases)).ToArray(),
                    diagnostics),
            ZType.ZNullableType { Inner: var inner } =>
                MapToClr(inner, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases) is { IsValueType: true } vt
                    ? typeof(Nullable<>).MakeGenericType(vt)
                    : MapToClr(inner, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases),
            ZType.ZFuncType ft => MakeFuncType(ft, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases),
            ZType.ZNamedType clrNt when clrNt.Name.Contains('.') =>
                ResolveClrNamedType(clrNt) ?? WarnAndFallbackToObject(diagnostics,
                    $"IlTypeMapper: Cannot map type '{type}' to CLR type, falling back to object"),
            _ => WarnAndFallbackToObject(diagnostics,
                $"IlTypeMapper: Cannot map type '{type}' to CLR type, falling back to object")
        };
    }

    private static Type MakeFuncType(ZType.ZFuncType ft, DiagnosticBag? diagnostics,
        TypeAliasRegistry? typeAliases = null)
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft.Params.Select(p => MapToClr(p, diagnostics, typeAliases)).ToArray();
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

        var types = ft.Params.Select(p => MapToClr(p, diagnostics, typeAliases))
            .Append(MapToClr(ft.Return, diagnostics, typeAliases)).ToArray();
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
        DiagnosticBag? diagnostics = null,
        TypeAliasRegistry? typeAliases = null)
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft.Params.Select(p => MapToClr(p, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases))
                .ToArray();
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

        var types = ft.Params.Select(p => MapToClr(p, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases))
            .Append(MapToClr(ft.Return, userTypes, typeParamMap, typeVarMap, diagnostics, typeAliases)).ToArray();
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
