using System.Collections.Immutable;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Maps ZScheme types to AsmResolver TypeSignature instances.
/// </summary>
public static class AsmResolverTypeMapper
{
    public static TypeSignature MapReturnTypeToClr(ZType type, ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes = null,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap = null,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap = null,
        TypeAliasRegistry? typeAliases = null,
        ClrInterop? clrInterop = null)
    {
        return type == ZType.Unit
            ? module.CorLibTypeFactory.Void
            : MapToClr(type, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop);
    }

    public static TypeSignature MapToClr(ZType type, ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes = null,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap = null,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap = null,
        TypeAliasRegistry? typeAliases = null,
        ClrInterop? clrInterop = null)
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
            ZType.ZNamedType nt when typeAliases is not null
                                     && typeAliases.TryGet(nt.Name, out var alias) && alias is not null =>
                ApplyAlias(alias, nt.TypeArgs, module, unitType, userTypes, typeParamMap, typeVarMap,
                    typeAliases, clrInterop),
            ZType.ZNamedType { Name: "ValueTuple", TypeArgs: { Count: > 0 and var vtCount } vtArgs } =>
                MakeValueTupleInstance(vtArgs, vtCount, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop),
            ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [] } =>
                ImportTypeCorLibAware(module, typeof(Task)).ToTypeSignature(false),
            ZType.ZNamedType { Name: "Task" or "System.Threading.Tasks.Task", TypeArgs: [var t] } =>
                MakeGenericInstance(module, typeof(Task<>),
                    [MapToClr(t, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop)]),
            ZType.ZNamedType nt when userTypes is not null && userTypes.TryGetValue(nt.Name, out var ut) =>
                nt.TypeArgs.Count > 0
                    ? ut.ToTypeDefOrRef().ToTypeSignature(ut.IsValueType)
                        .MakeGenericInstanceType(ut.IsValueType, nt.TypeArgs
                            .Select(ta => MapToClr(ta, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop))
                            .ToArray())
                    : ut,
            ZType.ZNullableType { Inner: var inner } =>
                MapToClrNullable(inner, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop),
            ZType.ZFuncType ft => MakeFuncType(ft, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop),
            ZType.ZNamedType clrNt when clrNt.Name.Contains('.') =>
                ResolveClrNamedType(clrNt, module) ?? module.CorLibTypeFactory.Object,
            _ => module.CorLibTypeFactory.Object
        };
    }

    /// <summary>
    ///     Resolves a <see cref="TypeAliasInfo"/> to a CLR <see cref="TypeSignature"/>, recursively
    ///     mapping the type arguments. Validates arity and emits <see cref="module"/>.Object on
    ///     mismatch (no diagnostic — the caller already validated when collecting the alias).
    /// </summary>
    private static TypeSignature ApplyAlias(TypeAliasInfo alias, IReadOnlyList<ZType> typeArgs,
        ModuleDefinition module, TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop)
    {
        if (typeArgs.Count != alias.TypeParams.Count)
            return module.CorLibTypeFactory.Object;

        var mappedArgs = typeArgs
            .Select(a => MapToClr(a, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop))
            .ToArray();

        if (alias.Kind == TypeAliasKind.SzArray)
            return new SzArrayTypeSignature(mappedArgs[0]);

        var clrType = ResolveAliasTarget(alias, clrInterop);
        if (clrType is null)
            return module.CorLibTypeFactory.Object;

        if (mappedArgs.Length == 0)
            return module.DefaultImporter.ImportType(clrType).ToTypeSignature(clrType.IsValueType);

        return MakeGenericInstance(module, clrType, mappedArgs);
    }

    private static Type? ResolveAliasTarget(TypeAliasInfo alias, ClrInterop? clrInterop)
    {
        var arity = alias.TypeParams.Count;
        // Open generic types are loaded as `OpenName`{arity}
        var openName = arity > 0 ? $"{alias.ClrTarget}`{arity}" : alias.ClrTarget;

        // Try the assembly hint first if provided
        if (alias.AssemblyHint is not null)
        {
            var hinted = Type.GetType($"{openName}, {alias.AssemblyHint}");
            if (hinted is not null) return hinted;
        }

        // Try direct + System.Runtime
        var direct = Type.GetType(openName) ?? Type.GetType($"{openName}, System.Runtime");
        if (direct is not null) return direct;

        // Search loaded assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(openName);
            if (t is not null) return t;
        }

        // Fall back to ClrInterop probing (handles search paths, runtime dir, etc.)
        return clrInterop?.FindType(openName);
    }

    /// <summary>
    ///     Maps a nullable type: Nullable&lt;T&gt; for value types, just T for reference types.
    /// </summary>
    private static TypeSignature MapToClrNullable(ZType inner, ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop)
    {
        var innerSig = MapToClr(inner, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop);
        // Only value types use Nullable<T>; reference types are already nullable
        if (innerSig.IsValueType)
            return MakeGenericInstance(module, typeof(Nullable<>), [innerSig]);
        return innerSig;
    }

    private static TypeSignature? ResolveClrNamedType(ZType.ZNamedType nt, ModuleDefinition module)
    {
        // Try to resolve fully-qualified CLR type names (e.g., System.DateTime, System.TimeSpan)
        var clrType = Type.GetType(nt.Name) ?? Type.GetType($"{nt.Name}, System.Runtime");
        if (clrType is null)
            // Search all loaded assemblies for the type (e.g., ZWorld.GameServer.Characters.Character)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                clrType = asm.GetType(nt.Name);
                if (clrType is not null) break;
            }

        if (clrType is not null)
            return module.DefaultImporter.ImportType(clrType).ToTypeSignature(clrType.IsValueType);
        return null;
    }

    private static GenericInstanceTypeSignature MakeValueTupleInstance(
        IReadOnlyList<ZType> vtArgs, int count, ModuleDefinition module, TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop)
    {
        var mappedArgs = vtArgs
            .Select(a => MapToClr(a, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop)).ToArray();
        var openType = count switch
        {
            1 => typeof(ValueTuple<>),
            2 => typeof(ValueTuple<,>),
            3 => typeof(ValueTuple<,,>),
            4 => typeof(ValueTuple<,,,>),
            5 => typeof(ValueTuple<,,,,>),
            6 => typeof(ValueTuple<,,,,,>),
            7 => typeof(ValueTuple<,,,,,,>),
            _ => typeof(ValueTuple<,>)
        };
        return MakeGenericInstance(module, openType, mappedArgs);
    }

    private static GenericInstanceTypeSignature MakeGenericInstance(ModuleDefinition module, Type openClrType,
        TypeSignature[] typeArgs)
    {
        var imported = ImportTypeCorLibAware(module, openClrType);
        return imported.ToTypeSignature(openClrType.IsValueType)
            .MakeGenericInstanceType(openClrType.IsValueType, typeArgs);
    }

    /// <summary>
    ///     Imports a CLR type, routing corlib types (Func, Action, Task, etc.) through the
    ///     module's configured corlib scope instead of System.Private.CoreLib.
    /// </summary>
    private static ITypeDefOrRef ImportTypeCorLibAware(ModuleDefinition module, Type clrType)
    {
        var imported = module.DefaultImporter.ImportType(clrType);
        var asmName = clrType.Assembly.GetName().Name;
        // Only reroute types that are actually forwarded through System.Runtime (the corlib scope).
        // Types in System.Collections.Generic (List<T>, Dictionary<K,V>, etc.) are forwarded
        // through System.Collections, not System.Runtime, so they must keep their original scope.
        // Types in System.Collections.Concurrent are forwarded through
        // System.Collections.Concurrent, not System.Runtime, so they must also keep their scope.
        // Exception: KeyValuePair<,> is in System.Collections.Generic but forwarded through
        // System.Runtime, so it must be rerouted.
        if (asmName is "System.Private.CoreLib" or "mscorlib"
            && clrType.Namespace is not "System.Collections.Concurrent"
            && (clrType.Namespace is not "System.Collections.Generic"
                || clrType.Name.StartsWith("KeyValuePair"))
            && imported is TypeReference tr)
            tr.Scope = module.CorLibTypeFactory.CorLibScope;
        return imported;
    }

    private static TypeSignature MakeFuncType(ZType.ZFuncType ft, ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop)
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft.Params.Select(p => MapToClr(p, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop))
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

        var types = ft.Params.Select(p => MapToClr(p, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop))
            .Append(MapToClr(ft.Return, module, unitType, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop)).ToArray();
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
