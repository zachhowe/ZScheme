using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     The single, backend-agnostic <see cref="ZType" /> → CLR-type traversal shared by the
///     reflection (<see cref="IlTypeMapper" />) and AsmResolver (<see cref="AsmResolverTypeMapper" />)
///     backends. All decision logic — alias resolution, Task/ValueTuple recognition, func/tuple
///     arity, nullable handling, name munging — lives here exactly once; the two backends differ
///     only in how a result is <em>constructed</em>, which is expressed through
///     <see cref="ITypeFactory{T}" />. This guarantees the two emitters provably agree on type
///     mapping (the property the differential fuzzer exists to enforce).
/// </summary>
internal static class TypeMapperCore
{
    public static T Map<T>(
        ZType type,
        ITypeFactory<T> f,
        IReadOnlyDictionary<string, T>? userTypes,
        IReadOnlyDictionary<string, T>? typeParamMap,
        IReadOnlyDictionary<int, T>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop
    )
        where T : class
    {
        switch (type)
        {
            case ZType.ZPrimitiveType prim:
                return f.Primitive(prim.Kind);

            case ZType.ZTypeVar tv
                when typeVarMap is not null && typeVarMap.TryGetValue(tv.Id, out var gp):
                return gp;

            case ZType.ZConstrainedVar cv
                when typeVarMap is not null && typeVarMap.TryGetValue(cv.Id, out var cgp):
                return cgp;

            case ZType.ZNamedType { TypeArgs: [] } tp
                when typeParamMap is not null && typeParamMap.TryGetValue(tp.Name, out var t):
                return t;

            // ValueTuple<...> — recognised by literal name OR the alias registry.
            case ZType.ZNamedType { TypeArgs: { Count: > 0 } vtArgs } vt
                when IsValueTuple(vt.Name, typeAliases):
            {
                var openTuple = ValueTupleOpenType(vtArgs.Count, out var tupleOverflow);
                return MapAndClose(
                    openTuple,
                    vtArgs,
                    f,
                    userTypes,
                    typeParamMap,
                    typeVarMap,
                    typeAliases,
                    clrInterop,
                    tupleOverflow,
                    $"ValueTuple with {vtArgs.Count} elements exceeds maximum of 7"
                );
            }

            // Task (non-generic) — recognised by literal name OR the alias registry.
            case ZType.ZNamedType { TypeArgs: [] } task when IsTask(task.Name, typeAliases):
                return f.FromClrType(typeof(Task), corLibAware: true);

            // Task<T>.
            case ZType.ZNamedType { TypeArgs: [var taskArg] } task
                when IsTask(task.Name, typeAliases):
                return f.CloseClrGeneric(
                    typeof(Task<>),
                    [Map(taskArg, f, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop)]
                );

            case ZType.ZNamedType nt
                when typeAliases is not null
                    && typeAliases.TryGet(nt.Name, out var alias)
                    && alias is not null:
                return ApplyAlias(
                    alias,
                    nt.TypeArgs,
                    f,
                    userTypes,
                    typeParamMap,
                    typeVarMap,
                    typeAliases,
                    clrInterop
                );

            case ZType.ZNamedType nt
                when userTypes is not null && userTypes.TryGetValue(nt.Name, out var ut):
                return nt.TypeArgs.Count > 0 && f.IsGenericDefinition(ut)
                    ? f.CloseMappedGeneric(
                        ut,
                        nt.TypeArgs.Select(a =>
                                Map(
                                    a,
                                    f,
                                    userTypes,
                                    typeParamMap,
                                    typeVarMap,
                                    typeAliases,
                                    clrInterop
                                )
                            )
                            .ToArray()
                    )
                    : ut;

            case ZType.ZNullableType { Inner: var inner }:
            {
                var innerMapped = Map(
                    inner,
                    f,
                    userTypes,
                    typeParamMap,
                    typeVarMap,
                    typeAliases,
                    clrInterop
                );
                return f.IsValueType(innerMapped)
                    ? f.CloseClrGeneric(typeof(Nullable<>), [innerMapped])
                    : innerMapped;
            }

            case ZType.ZDelegateType dt:
            {
                var clrType = ResolveDelegateType(dt.ClrTypeName);
                if (clrType is null)
                {
                    f.Warn($"TypeMapper: Cannot resolve delegate type '{dt.ClrTypeName}'");
                    return f.Object;
                }

                if (!typeof(Delegate).IsAssignableFrom(clrType))
                {
                    f.Warn($"TypeMapper: Type '{dt.ClrTypeName}' is not a delegate type");
                    return f.Object;
                }

                return f.FromClrType(clrType, corLibAware: false);
            }

            case ZType.ZFuncType ft:
                return MapFuncType(
                    ft,
                    f,
                    userTypes,
                    typeParamMap,
                    typeVarMap,
                    typeAliases,
                    clrInterop
                );

            case ZType.ZNamedType clrNt when clrNt.Name.Contains('.'):
            {
                var resolved = ResolveClrNamedType(
                    clrNt,
                    f,
                    userTypes,
                    typeParamMap,
                    typeVarMap,
                    typeAliases,
                    clrInterop
                );
                if (resolved is not null)
                    return resolved;
                f.Warn($"TypeMapper: Cannot map type '{type}' to CLR type, falling back to object");
                return f.Object;
            }

            default:
                f.Warn($"TypeMapper: Cannot map type '{type}' to CLR type, falling back to object");
                return f.Object;
        }
    }

    private static bool IsTask(string name, TypeAliasRegistry? typeAliases)
    {
        return (typeAliases?.IsTaskName(name) ?? false)
            || name is "Task" or "System.Threading.Tasks.Task";
    }

    private static bool IsValueTuple(string name, TypeAliasRegistry? typeAliases)
    {
        return (typeAliases?.IsValueTupleName(name) ?? false) || name == "ValueTuple";
    }

    /// <summary>
    ///     Returns the open <c>ValueTuple&lt;...&gt;</c> definition for the given arity, or signals
    ///     overflow (&gt; 7) via <paramref name="overflow" />.
    /// </summary>
    private static Type ValueTupleOpenType(int count, out bool overflow)
    {
        overflow = false;
        switch (count)
        {
            case 1:
                return typeof(ValueTuple<>);
            case 2:
                return typeof(ValueTuple<,>);
            case 3:
                return typeof(ValueTuple<,,>);
            case 4:
                return typeof(ValueTuple<,,,>);
            case 5:
                return typeof(ValueTuple<,,,,>);
            case 6:
                return typeof(ValueTuple<,,,,,>);
            case 7:
                return typeof(ValueTuple<,,,,,,>);
            default:
                overflow = true;
                return typeof(ValueTuple<,>);
        }
    }

    /// <summary>
    ///     Maps the type arguments and closes the given open generic CLR type over them, unless
    ///     <paramref name="overflow" /> is set, in which case it warns and falls back to object.
    /// </summary>
    private static T MapAndClose<T>(
        Type openClrType,
        IReadOnlyList<ZType> args,
        ITypeFactory<T> f,
        IReadOnlyDictionary<string, T>? userTypes,
        IReadOnlyDictionary<string, T>? typeParamMap,
        IReadOnlyDictionary<int, T>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop,
        bool overflow,
        string overflowMessage
    )
        where T : class
    {
        if (overflow)
        {
            f.Warn($"TypeMapper: {overflowMessage}, falling back to object");
            return f.Object;
        }

        var mapped = args.Select(a =>
                Map(a, f, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop)
            )
            .ToArray();
        return f.CloseClrGeneric(openClrType, mapped);
    }

    private static T ApplyAlias<T>(
        TypeAliasInfo alias,
        IReadOnlyList<ZType> typeArgs,
        ITypeFactory<T> f,
        IReadOnlyDictionary<string, T>? userTypes,
        IReadOnlyDictionary<string, T>? typeParamMap,
        IReadOnlyDictionary<int, T>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop
    )
        where T : class
    {
        if (typeArgs.Count != alias.TypeParams.Count)
        {
            f.Warn(
                $"TypeMapper: Alias '{alias.Name}' expects {alias.TypeParams.Count} type args, got {typeArgs.Count}"
            );
            return f.Object;
        }

        var mapped = typeArgs
            .Select(a => Map(a, f, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop))
            .ToArray();

        if (alias.Kind == TypeAliasKind.SzArray)
            return f.MakeArray(mapped[0]);

        var openType = ResolveAliasTarget(alias, clrInterop);
        if (openType is null)
        {
            f.Warn(
                $"TypeMapper: Cannot resolve CLR type for alias '{alias.Name}' -> '{alias.ClrTarget}'"
            );
            return f.Object;
        }

        return mapped.Length == 0
            ? f.FromClrType(openType, corLibAware: false)
            : f.CloseClrGeneric(openType, mapped);
    }

    private static T MapFuncType<T>(
        ZType.ZFuncType ft,
        ITypeFactory<T> f,
        IReadOnlyDictionary<string, T>? userTypes,
        IReadOnlyDictionary<string, T>? typeParamMap,
        IReadOnlyDictionary<int, T>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop
    )
        where T : class
    {
        if (ft.Return is ZType.ZPrimitiveType { Kind: PrimitiveKind.Unit })
        {
            var paramTypes = ft
                .Params.Select(p =>
                    Map(p, f, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop)
                )
                .ToArray();
            switch (paramTypes.Length)
            {
                case 0:
                    return f.FromClrType(typeof(Action), corLibAware: true);
                case 1:
                    return f.CloseClrGeneric(typeof(Action<>), paramTypes);
                case 2:
                    return f.CloseClrGeneric(typeof(Action<,>), paramTypes);
                case 3:
                    return f.CloseClrGeneric(typeof(Action<,,>), paramTypes);
                case 4:
                    return f.CloseClrGeneric(typeof(Action<,,,>), paramTypes);
                default:
                    f.Warn(
                        $"TypeMapper: Action delegate with {paramTypes.Length} parameters exceeds maximum of 4, falling back to object"
                    );
                    return f.Object;
            }
        }

        var types = ft
            .Params.Select(p =>
                Map(p, f, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop)
            )
            .Append(Map(ft.Return, f, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop))
            .ToArray();
        switch (types.Length)
        {
            case 1:
                return f.CloseClrGeneric(typeof(Func<>), types);
            case 2:
                return f.CloseClrGeneric(typeof(Func<,>), types);
            case 3:
                return f.CloseClrGeneric(typeof(Func<,,>), types);
            case 4:
                return f.CloseClrGeneric(typeof(Func<,,,>), types);
            case 5:
                return f.CloseClrGeneric(typeof(Func<,,,,>), types);
            default:
                f.Warn(
                    $"TypeMapper: Func delegate with {types.Length} type arguments exceeds maximum of 5, falling back to object"
                );
                return f.Object;
        }
    }

    private static T? ResolveClrNamedType<T>(
        ZType.ZNamedType nt,
        ITypeFactory<T> f,
        IReadOnlyDictionary<string, T>? userTypes,
        IReadOnlyDictionary<string, T>? typeParamMap,
        IReadOnlyDictionary<int, T>? typeVarMap,
        TypeAliasRegistry? typeAliases,
        ClrInterop? clrInterop
    )
        where T : class
    {
        // A generic CLR type (e.g. System.Collections.Generic.ICollection<string>) is named here
        // without the reflection arity suffix, so resolve the open `Name`N` definition and close
        // it over the recursively-mapped type arguments.
        if (nt.TypeArgs.Count > 0)
        {
            var open = FindClrType($"{nt.Name}`{nt.TypeArgs.Count}");
            if (open is null)
                return null;
            var args = nt
                .TypeArgs.Select(a =>
                    Map(a, f, userTypes, typeParamMap, typeVarMap, typeAliases, clrInterop)
                )
                .ToArray();
            try
            {
                return f.CloseClrGeneric(open, args);
            }
            catch
            {
                return null;
            }
        }

        var clrType = FindClrType(nt.Name);
        return clrType is null ? null : f.FromClrType(clrType, corLibAware: false);
    }

    // --- Shared, backend-independent CLR `System.Type` resolution helpers ---

    private static Type? FindClrType(string name)
    {
        // Try to resolve fully-qualified CLR type names (e.g. System.DateTime), then search all
        // loaded assemblies (e.g. user types like ZWorld.GameServer.Characters.Character).
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

    private static Type? ResolveAliasTarget(TypeAliasInfo alias, ClrInterop? clrInterop)
    {
        var arity = alias.TypeParams.Count;
        // Open generic types are loaded as `OpenName`{arity}.
        var openName = arity > 0 ? $"{alias.ClrTarget}`{arity}" : alias.ClrTarget;

        // Try the assembly hint first if provided.
        if (alias.AssemblyHint is not null)
        {
            var hinted = Type.GetType($"{openName}, {alias.AssemblyHint}");
            if (hinted is not null)
                return hinted;
        }

        // Try direct + System.Runtime.
        var direct = Type.GetType(openName) ?? Type.GetType($"{openName}, System.Runtime");
        if (direct is not null)
            return direct;

        // Search loaded assemblies.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(openName);
            if (t is not null)
                return t;
        }

        // Fall back to ClrInterop probing (handles search paths, runtime dir, etc.).
        return clrInterop?.FindType(openName);
    }

    private static Type? ResolveDelegateType(string clrTypeName)
    {
        // Convert C#-style generic type names (System.Func<int,int>) to .NET reflection names.
        var reflectionName = ClrTypeNames.ConvertToReflectionTypeName(clrTypeName);

        // Type.GetType interprets ',' as a type/assembly separator, so for generic types we need
        // to search assemblies directly.
        if (reflectionName.Contains(','))
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(reflectionName);
                if (t is not null)
                    return t;
            }

            return null;
        }

        return Type.GetType(reflectionName) ?? Type.GetType($"{reflectionName}, System.Runtime");
    }
}
