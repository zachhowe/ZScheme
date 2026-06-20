using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Maps ZScheme types to AsmResolver TypeSignature instances.
/// </summary>
public static class AsmResolverTypeMapper
{
    public static TypeSignature MapReturnTypeToClr(
        ZType type,
        ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes = null,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap = null,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap = null,
        TypeAliasRegistry? typeAliases = null,
        ClrInterop? clrInterop = null
    )
    {
        return type == ZType.Unit
            ? module.CorLibTypeFactory.Void
            : MapToClr(
                type,
                module,
                unitType,
                userTypes,
                typeParamMap,
                typeVarMap,
                typeAliases,
                clrInterop
            );
    }

    public static TypeSignature MapToClr(
        ZType type,
        ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes = null,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap = null,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap = null,
        TypeAliasRegistry? typeAliases = null,
        ClrInterop? clrInterop = null
    )
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
            ZType.ZTypeVar tv
                when typeVarMap is not null && typeVarMap.TryGetValue(tv.Id, out var gp) => gp,
            ZType.ZConstrainedVar cv
                when typeVarMap is not null && typeVarMap.TryGetValue(cv.Id, out var cgp) => cgp,
            ZType.ZNamedType { TypeArgs: [] } nt
                when typeParamMap is not null && typeParamMap.TryGetValue(nt.Name, out var tp) =>
                tp,
            ZType.ZNamedType { TypeArgs: { Count: > 0 and var vtCount } vtArgs } vt3
                when typeAliases is not null && typeAliases.IsValueTupleName(vt3.Name) =>
                MakeValueTupleInstance(
                    vtArgs,
                    vtCount,
                    module,
                    unitType,
                    userTypes,
                    typeParamMap,
                    typeVarMap,
                    typeAliases,
                    clrInterop
                ),
            ZType.ZNamedType { TypeArgs: [] } task5
                when typeAliases is not null && typeAliases.IsTaskName(task5.Name) =>
                ImportTypeCorLibAware(module, typeof(Task)).ToTypeSignature(false),
            ZType.ZNamedType { TypeArgs: [var t] } task6
                when typeAliases is not null && typeAliases.IsTaskName(task6.Name) =>
                MakeGenericInstance(
                    module,
                    typeof(Task<>),
                    [
                        MapToClr(
                            t,
                            module,
                            unitType,
                            userTypes,
                            typeParamMap,
                            typeVarMap,
                            typeAliases,
                            clrInterop
                        ),
                    ]
                ),
            ZType.ZNamedType nt
                when typeAliases is not null
                    && typeAliases.TryGet(nt.Name, out var alias)
                    && alias is not null => ApplyAlias(
                alias,
                nt.TypeArgs,
                module,
                unitType,
                userTypes,
                typeParamMap,
                typeVarMap,
                typeAliases,
                clrInterop
            ),
            ZType.ZNamedType nt
                when userTypes is not null && userTypes.TryGetValue(nt.Name, out var ut) =>
                nt.TypeArgs.Count > 0
                    ? ut.ToTypeDefOrRef()
                        .ToTypeSignature(ut.IsValueType)
                        .MakeGenericInstanceType(
                            ut.IsValueType,
                            nt.TypeArgs.Select(ta =>
                                    MapToClr(
                                        ta,
                                        module,
                                        unitType,
                                        userTypes,
                                        typeParamMap,
                                        typeVarMap,
                                        typeAliases,
                                        clrInterop
                                    )
                                )
                                .ToArray()
                        )
                    : ut,
            ZType.ZNullableType { Inner: var inner } => MapToClrNullable(
                inner,
                module,
                unitType,
                userTypes,
                typeParamMap,
                typeVarMap,
                typeAliases,
                clrInterop
            ),
            ZType.ZDelegateType dt => ResolveDelegateSignature(dt.ClrTypeName, module),
            ZType.ZFuncType ft => MakeFuncType(
                ft,
                module,
                unitType,
                userTypes,
                typeParamMap,
                typeVarMap,
                typeAliases,
                clrInterop
            ),
            ZType.ZNamedType clrNt when clrNt.Name.Contains('.') => ResolveClrNamedType(
                clrNt,
                module,
                unitType,
                userTypes,
                typeParamMap,
                typeVarMap,
                typeAliases,
                clrInterop
            ) ?? module.CorLibTypeFactory.Object,
            _ => module.CorLibTypeFactory.Object,
        };
    }

    /// <summary>
    ///     Resolves a <see cref="TypeAliasInfo" /> to a CLR <see cref="TypeSignature" />, recursively
    ///     mapping the type arguments. Validates arity and emits <see cref="module" />.Object on
    ///     mismatch (no diagnostic — the caller already validated when collecting the alias).
    /// </summary>
    private static TypeSignature ApplyAlias(
        TypeAliasInfo alias,
        IReadOnlyList<ZType> typeArgs,
        ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop
    )
    {
        if (typeArgs.Count != alias.TypeParams.Count)
            return module.CorLibTypeFactory.Object;

        var mappedArgs = typeArgs
            .Select(a =>
                MapToClr(
                    a,
                    module,
                    unitType,
                    userTypes,
                    typeParamMap,
                    typeVarMap,
                    typeAliases,
                    clrInterop
                )
            )
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
            if (hinted is not null)
                return hinted;
        }

        // Try direct + System.Runtime
        var direct = Type.GetType(openName) ?? Type.GetType($"{openName}, System.Runtime");
        if (direct is not null)
            return direct;

        // Search loaded assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(openName);
            if (t is not null)
                return t;
        }

        // Fall back to ClrInterop probing (handles search paths, runtime dir, etc.)
        return clrInterop?.FindType(openName);
    }

    /// <summary>
    ///     Maps a nullable type: Nullable&lt;T&gt; for value types, just T for reference types.
    /// </summary>
    private static TypeSignature MapToClrNullable(
        ZType inner,
        ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop
    )
    {
        var innerSig = MapToClr(
            inner,
            module,
            unitType,
            userTypes,
            typeParamMap,
            typeVarMap,
            typeAliases,
            clrInterop
        );
        // Only value types use Nullable<T>; reference types are already nullable
        if (innerSig.IsValueType)
            return MakeGenericInstance(module, typeof(Nullable<>), [innerSig]);
        return innerSig;
    }

    private static TypeSignature? ResolveClrNamedType(
        ZType.ZNamedType nt,
        ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop
    )
    {
        // A generic CLR type (e.g. System.Collections.Generic.ICollection<string>) is named
        // without the reflection arity suffix; resolve the open `Name`N` and close it over the
        // recursively-mapped type arguments.
        if (nt.TypeArgs.Count > 0)
        {
            var open = FindClrType($"{nt.Name}`{nt.TypeArgs.Count}");
            if (open is null)
                return null;
            var args = nt
                .TypeArgs.Select(a =>
                    MapToClr(
                        a,
                        module,
                        unitType,
                        userTypes,
                        typeParamMap,
                        typeVarMap,
                        typeAliases,
                        clrInterop
                    )
                )
                .ToArray();
            return MakeGenericInstance(module, open, args);
        }

        var clrType = FindClrType(nt.Name);
        return clrType is null
            ? null
            : module.DefaultImporter.ImportType(clrType).ToTypeSignature(clrType.IsValueType);
    }

    private static Type? FindClrType(string name)
    {
        // Try to resolve fully-qualified CLR type names (e.g. System.DateTime), then search
        // all loaded assemblies (e.g. user types like ZWorld.GameServer.Characters.Character).
        var clrType = Type.GetType(name) ?? Type.GetType($"{name}, System.Runtime");
        if (clrType is null)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                clrType = asm.GetType(name);
                if (clrType is not null)
                    break;
            }

        return clrType;
    }

    private static TypeSignature ResolveDelegateSignature(
        string clrTypeName,
        ModuleDefinition module
    )
    {
        // Convert C#-style generic type names to .NET reflection type names
        var reflectionName = ConvertToReflectionTypeName(clrTypeName);

        // Type.GetType interprets ',' as a type/assembly separator, so for generic types
        // we need to search assemblies directly
        Type? clrType = null;
        if (reflectionName.Contains(','))
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                clrType = asm.GetType(reflectionName);
                if (clrType is not null)
                    break;
            }
        }
        else
        {
            clrType =
                Type.GetType(reflectionName) ?? Type.GetType($"{reflectionName}, System.Runtime");
        }

        if (clrType is null)
            return module.CorLibTypeFactory.Object;

        if (!typeof(Delegate).IsAssignableFrom(clrType))
            return module.CorLibTypeFactory.Object;

        return module.DefaultImporter.ImportType(clrType).ToTypeSignature(false);
    }

    /// <summary>
    ///     Converts a C#-style generic type name (e.g. <c>System.Func&lt;int,int&gt;</c>) into the
    ///     reflection form (<c>System.Func`2[System.Int32,System.Int32]</c>) that
    ///     <see cref="Type.GetType(string)" />/<c>Assembly.GetType</c> understand. Names without
    ///     angle brackets are returned unchanged.
    /// </summary>
    public static string ConvertToReflectionTypeName(string typeName)
    {
        if (!typeName.Contains('<'))
            return typeName;

        var openAngle = typeName.IndexOf('<');
        var closeAngle = typeName.LastIndexOf('>');
        if (openAngle >= closeAngle)
            return typeName;

        var baseName = typeName[..openAngle];
        var typeArgsStr = typeName[(openAngle + 1)..closeAngle];

        var backtick = baseName.LastIndexOf('`');
        var arity = typeArgsStr.Split(',').Length;

        string reflectedBase;
        if (backtick > 0)
            reflectedBase = baseName[..backtick];
        else
            reflectedBase = $"{baseName}`{arity}";

        var reflectedArgs = typeArgsStr.Split(',').Select(ConvertTypeArg).ToArray();

        return $"{reflectedBase}[{string.Join(",", reflectedArgs)}]";
    }

    private static string ConvertTypeArg(string arg)
    {
        arg = arg.Trim();
        return arg switch
        {
            "int" or "Int32" => "System.Int32",
            "long" or "Int64" => "System.Int64",
            "short" or "Int16" => "System.Int16",
            "byte" or "Byte" or "uint" or "UInt32" => "System.UInt32",
            "ushort" or "UInt16" => "System.UInt16",
            "sbyte" or "SByte" => "System.SByte",
            "float" or "Single" => "System.Single",
            "double" or "Double" => "System.Double",
            "bool" or "Boolean" => "System.Boolean",
            "string" or "String" => "System.String",
            "char" or "Char" => "System.Char",
            "unit" or "Unit" => "System.Object",
            _ => arg,
        };
    }

    private static GenericInstanceTypeSignature MakeValueTupleInstance(
        IReadOnlyList<ZType> vtArgs,
        int count,
        ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop
    )
    {
        var mappedArgs = vtArgs
            .Select(a =>
                MapToClr(
                    a,
                    module,
                    unitType,
                    userTypes,
                    typeParamMap,
                    typeVarMap,
                    typeAliases,
                    clrInterop
                )
            )
            .ToArray();
        var openType = count switch
        {
            1 => typeof(ValueTuple<>),
            2 => typeof(ValueTuple<,>),
            3 => typeof(ValueTuple<,,>),
            4 => typeof(ValueTuple<,,,>),
            5 => typeof(ValueTuple<,,,,>),
            6 => typeof(ValueTuple<,,,,,>),
            7 => typeof(ValueTuple<,,,,,,>),
            _ => typeof(ValueTuple<,>),
        };
        return MakeGenericInstance(module, openType, mappedArgs);
    }

    private static GenericInstanceTypeSignature MakeGenericInstance(
        ModuleDefinition module,
        Type openClrType,
        TypeSignature[] typeArgs
    )
    {
        var imported = ImportTypeCorLibAware(module, openClrType);
        return imported
            .ToTypeSignature(openClrType.IsValueType)
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
        if (
            asmName is "System.Private.CoreLib" or "mscorlib"
            && clrType.Namespace is not "System.Collections.Concurrent"
            && (
                clrType.Namespace is not "System.Collections.Generic"
                || clrType.Name.StartsWith("KeyValuePair")
            )
            && imported is TypeReference tr
        )
            tr.Scope = module.CorLibTypeFactory.CorLibScope;
        return imported;
    }

    private static TypeSignature MakeFuncType(
        ZType.ZFuncType ft,
        ModuleDefinition module,
        TypeSignature unitType,
        IReadOnlyDictionary<string, TypeSignature>? userTypes,
        IReadOnlyDictionary<string, TypeSignature>? typeParamMap,
        IReadOnlyDictionary<int, TypeSignature>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop
    )
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft
                .Params.Select(p =>
                    MapToClr(
                        p,
                        module,
                        unitType,
                        userTypes,
                        typeParamMap,
                        typeVarMap,
                        typeAliases,
                        clrInterop
                    )
                )
                .ToArray();
            if (paramTypes.Length == 0)
                return ImportTypeCorLibAware(module, typeof(Action)).ToTypeSignature(false);
            var actionOpenType = paramTypes.Length switch
            {
                1 => typeof(Action<>),
                2 => typeof(Action<,>),
                3 => typeof(Action<,,>),
                4 => typeof(Action<,,,>),
                _ => typeof(object),
            };
            if (actionOpenType == typeof(object))
                return module.CorLibTypeFactory.Object;
            return MakeGenericInstance(module, actionOpenType, paramTypes);
        }

        var types = ft
            .Params.Select(p =>
                MapToClr(
                    p,
                    module,
                    unitType,
                    userTypes,
                    typeParamMap,
                    typeVarMap,
                    typeAliases,
                    clrInterop
                )
            )
            .Append(
                MapToClr(
                    ft.Return,
                    module,
                    unitType,
                    userTypes,
                    typeParamMap,
                    typeVarMap,
                    typeAliases,
                    clrInterop
                )
            )
            .ToArray();
        var funcOpenType = types.Length switch
        {
            1 => typeof(Func<>),
            2 => typeof(Func<,>),
            3 => typeof(Func<,,>),
            4 => typeof(Func<,,,>),
            5 => typeof(Func<,,,,>),
            _ => typeof(object),
        };
        if (funcOpenType == typeof(object))
            return module.CorLibTypeFactory.Object;
        return MakeGenericInstance(module, funcOpenType, types);
    }
}
