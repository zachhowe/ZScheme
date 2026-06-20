using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Codegen;

public sealed class ClrInterop : IDisposable
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Func<AssemblyLoadContext, AssemblyName, Assembly?> _resolveHandler;
    private readonly IReadOnlyList<string> _searchPaths;
    private readonly TypeAliasRegistry _typeAliases;

    public ClrInterop(
        DiagnosticBag diagnostics,
        IReadOnlyList<string>? assemblySearchPaths = null,
        TypeAliasRegistry? typeAliases = null
    )
    {
        _diagnostics = diagnostics;
        _searchPaths = assemblySearchPaths ?? [];
        _typeAliases = typeAliases ?? new TypeAliasRegistry();

        // Register an assembly resolution handler so that transitive dependencies of
        // assemblies loaded from search paths can be found.
        _resolveHandler = (context, assemblyName) =>
        {
            var simpleName = assemblyName.Name;
            if (simpleName is null)
                return null;

            foreach (var searchPath in _searchPaths)
            {
                if (!Directory.Exists(searchPath))
                    continue;

                var candidate = Path.Combine(searchPath, simpleName + ".dll");
                if (File.Exists(candidate))
                    try
                    {
                        return context.LoadFromAssemblyPath(Path.GetFullPath(candidate));
                    }
                    catch
                    {
                        // ignore
                    }
            }

            return null;
        };
        AssemblyLoadContext.Default.Resolving += _resolveHandler;
    }

    public void Dispose()
    {
        AssemblyLoadContext.Default.Resolving -= _resolveHandler;
    }

    /// <summary>
    ///     Resolves "System.Math/Sqrt" to a MethodInfo.
    ///     Format: TypeFullName/MethodName
    /// </summary>
    public MethodInfo? Resolve(string qualifiedName, SourceSpan span)
    {
        var slashIndex = qualifiedName.LastIndexOf('/');
        if (slashIndex < 0)
        {
            _diagnostics.Error(
                $"Invalid CLR reference: '{qualifiedName}'. Expected Type/Method format.",
                span
            );
            return null;
        }

        var typeName = qualifiedName[..slashIndex];
        var methodName = qualifiedName[(slashIndex + 1)..];

        var type = FindType(typeName);
        if (type is null)
        {
            _diagnostics.Error($"CLR type not found: '{typeName}'", span);
            return null;
        }

        MethodInfo? method;
        try
        {
            method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        }
        catch (AmbiguousMatchException)
        {
            method = PickBestOverload(type, methodName, BindingFlags.Public | BindingFlags.Static);
        }

        if (method is null)
            try
            {
                method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (AmbiguousMatchException)
            {
                method = PickBestOverload(
                    type,
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance
                );
            }

        if (method is null)
        {
            _diagnostics.Error($"CLR method not found: '{methodName}' on type '{typeName}'", span);
            return null;
        }

        return method;
    }

    /// <summary>
    ///     Resolves the best overload for a CLR method call at the call site using
    ///     the resolved function type from type inference. Picks the candidate whose
    ///     declared signature unifies with the resolved function type. When multiple
    ///     candidates match with the same return type, picks the last one (matches
    ///     the legacy ResolveOverload behavior for interchangeable signatures).
    /// </summary>
    public MethodInfo? ResolveOverloadCallSite(
        string typeName,
        string methodName,
        ZType resolvedFuncType,
        SourceSpan span
    )
    {
        var type = FindType(typeName);
        if (type is null)
        {
            _diagnostics.Error($"CLR type not found: '{typeName}'", span);
            return null;
        }

        var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == methodName)
            .ToList();

        if (candidates.Count == 0)
            // No methods with this name — could be a field, property, or just
            // a non-static member. Return null to let the existing fallback
            // (string-based emission) handle it without emitting an error.
            return null;

        // Convert each candidate to a ZFuncType and try speculative unification
        // against the resolved function type (same pattern as TypeInferer.ResolveOverload).
        var matches = new List<MethodInfo>();
        foreach (var candidate in candidates)
        {
            var candidateZType = MethodInfoToZFuncType(candidate);
            var scratchDiag = new DiagnosticBag();
            var scratchUnifier = new Unifier(new Substitution(), scratchDiag);
            var ok =
                scratchUnifier.Unify(resolvedFuncType, candidateZType, span)
                && !scratchDiag.HasErrors;
            if (ok)
                matches.Add(candidate);
        }

        if (matches.Count == 0)
            return null;

        if (matches.Count == 1)
            return matches[0];

        // Delegate-specificity tie-break: when an argument is a function/delegate,
        // prefer candidates whose corresponding parameter is a concrete delegate that
        // structurally (or nominally) matches over candidates taking the abstract
        // System.Delegate base. This is what lets `MapGet(..., RequestDelegate)` win
        // over `MapGet(..., Delegate)`. No-op when no argument is function/delegate-typed.
        if (resolvedFuncType is ZType.ZFuncType rft)
        {
            var specific = matches.Where(m => IsDelegateShapeSpecific(m, rft, span)).ToList();
            if (specific.Count > 0 && specific.Count < matches.Count)
            {
                matches = specific;
                if (matches.Count == 1)
                    return matches[0];
            }
        }

        // Multiple matches with the same return type: pick the most specific by parameter
        // shape (a more-derived parameter type wins), with a stable ordinal tie-break so
        // resolution is deterministic regardless of the reflection method ordering.
        // Differing return types remain genuinely ambiguous.
        var firstRet = ZType.Format(MapClrTypeToZType(matches[0].ReturnType));
        var allEquivalent = matches.All(m =>
            ZType.Format(MapClrTypeToZType(m.ReturnType)) == firstRet
        );
        if (!allEquivalent)
        {
            var qualifiedRef = $"{typeName}/{methodName}";
            var candList = string.Join(
                ", ",
                matches.Select(m =>
                    $"{qualifiedRef}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})"
                )
            );
            _diagnostics.Error(
                $"Ambiguous overload of '{qualifiedRef}'; candidates: {candList}. Qualify the call site explicitly.",
                span
            );
            return null;
        }

        var argParams = (resolvedFuncType as ZType.ZFuncType)?.Params;
        return matches
            // Primary: most parameters that structurally match the argument types.
            .OrderByDescending(m => ExactMatchCount(m, argParams))
            // Secondary: CLR "more specific" relation (a more-derived parameter type, e.g.
            // object[] over object, wins) computed pairwise across the candidate set.
            .ThenByDescending(m => PairwiseSpecificity(m, matches))
            // Final: stable ordinal tie-break so the result never depends on reflection order.
            .ThenBy(
                m => string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName)),
                StringComparer.Ordinal
            )
            .First();
    }

    private int ExactMatchCount(MethodInfo candidate, IReadOnlyList<ZType>? argParams)
    {
        if (argParams is null)
            return 0;

        var count = 0;
        var ps = candidate.GetParameters();
        for (var i = 0; i < ps.Length && i < argParams.Count; i++)
            if (ZType.Format(MapClrTypeToZType(ps[i].ParameterType)) == ZType.Format(argParams[i]))
                count++;
        return count;
    }

    /// <summary>
    ///     Scores how specific a candidate is relative to the others using the CLR betterness
    ///     relation: a parameter that is more derived than the corresponding parameter of
    ///     another candidate (i.e. assignable TO it) is more specific. Mirrors how C# prefers
    ///     the most specific overload (e.g. an object[] parameter over an object parameter).
    /// </summary>
    private static int PairwiseSpecificity(MethodInfo candidate, IReadOnlyList<MethodInfo> all)
    {
        var ps = candidate.GetParameters();
        var score = 0;
        foreach (var other in all)
        {
            if (ReferenceEquals(other, candidate))
                continue;
            var ops = other.GetParameters();
            for (var i = 0; i < ps.Length && i < ops.Length; i++)
            {
                var a = ps[i].ParameterType;
                var b = ops[i].ParameterType;
                if (a == b)
                    continue;
                if (b.IsAssignableFrom(a))
                    score++; // candidate's param is more derived
                else if (a.IsAssignableFrom(b))
                    score--; // candidate's param is more general
            }
        }

        return score;
    }

    public ZType MapClrTypeToZType(Type clrType)
    {
        if (clrType == typeof(int))
            return ZType.Int;
        if (clrType == typeof(long))
            return ZType.Long;
        if (clrType == typeof(float))
            return ZType.Float;
        if (clrType == typeof(double))
            return ZType.Double;
        if (clrType == typeof(byte))
            return ZType.Byte;
        if (clrType == typeof(char))
            return ZType.Char;
        if (clrType == typeof(bool))
            return ZType.Bool;
        if (clrType == typeof(string))
            return ZType.String;
        if (clrType == typeof(void))
            return ZType.Unit;

        if (typeof(Delegate).IsAssignableFrom(clrType))
            return new ZType.ZDelegateType(clrType.FullName ?? clrType.Name);

        // Use registry for known type aliases (collections, Task, arrays, etc.)
        if (clrType.IsArray)
        {
            if (_typeAliases.TryGetZsNameFromClrType(clrType, out var zsName))
                return new ZType.ZNamedType(
                    zsName!,
                    [MapClrTypeToZType(clrType.GetElementType()!)]
                );
            if (_typeAliases.TryGetFirstArrayAliasName(out var arrayName))
                return new ZType.ZNamedType(
                    arrayName!,
                    [MapClrTypeToZType(clrType.GetElementType()!)]
                );
            return new ZType.ZNamedType(
                "Clr-Array",
                [MapClrTypeToZType(clrType.GetElementType()!)]
            );
        }

        if (clrType.IsGenericType)
        {
            if (_typeAliases.TryGetZsNameFromClrType(clrType, out var zsName2))
            {
                var args = clrType.GetGenericArguments();
                return new ZType.ZNamedType(zsName2!, args.Select(MapClrTypeToZType).ToList());
            }

            if (clrType.GetGenericTypeDefinition() == typeof(Task<>))
                return new ZType.ZNamedType(
                    "Task",
                    [MapClrTypeToZType(clrType.GetGenericArguments()[0])]
                );
        }

        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(Nullable<>))
            return new ZType.ZNullableType(MapClrTypeToZType(clrType.GetGenericArguments()[0]));

        return new ZType.ZNamedType(clrType.FullName ?? clrType.Name, []);
    }

    public MethodInfo? ResolveGeneric(string qualifiedName, int genericArity, SourceSpan span)
    {
        var slashIndex = qualifiedName.LastIndexOf('/');
        if (slashIndex < 0)
        {
            _diagnostics.Error(
                $"Invalid CLR reference: '{qualifiedName}'. Expected Type/Method format.",
                span
            );
            return null;
        }

        var typeName = qualifiedName[..slashIndex];
        var methodName = qualifiedName[(slashIndex + 1)..];

        var type = FindType(typeName);
        if (type is null)
        {
            _diagnostics.Error($"CLR type not found: '{typeName}'", span);
            return null;
        }

        var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m =>
                m.Name == methodName
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == genericArity
            )
            .ToList();

        if (candidates.Count == 0)
        {
            _diagnostics.Error(
                $"No generic method '{methodName}' with {genericArity} type parameter(s) on '{typeName}'",
                span
            );
            return null;
        }

        // Prefer overloads where all parameters are plain generic type parameters (e.g. T, T)
        // over overloads where parameters are constructed types (e.g. IEnumerable<T>, IEnumerable<T>)
        var preferred = candidates
            .Where(m => m.GetParameters().All(p => p.ParameterType.IsGenericParameter))
            .ToList();
        return preferred.Count > 0
            ? preferred.OrderBy(m => m.GetParameters().Length).First()
            : candidates.OrderBy(m => m.GetParameters().Length).First();
    }

    public ZType GenericMethodInfoToZFuncType(MethodInfo method, IReadOnlyList<int> typeVarIds)
    {
        var genericArgs = method.GetGenericArguments();
        var mapping = new Dictionary<Type, ZType>();
        for (var i = 0; i < genericArgs.Length; i++)
            mapping[genericArgs[i]] = new ZType.ZTypeVar(typeVarIds[i]);

        var paramTypes = method
            .GetParameters()
            .Select(p => MapClrTypeWithGenerics(p.ParameterType, mapping))
            .ToList();
        var returnType = MapClrTypeWithGenerics(method.ReturnType, mapping);
        return new ZType.ZFuncType(paramTypes, returnType);
    }

    /// <summary>
    ///     Resolves an instance method from its qualified name (Type.Method or Type/Method)
    ///     and returns out-parameter metadata, if any.
    /// </summary>
    public IReadOnlyList<OutParamInfo> DetectOutParams(
        string qualifiedName,
        SourceSpan span,
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance
    )
    {
        // Split on last '/' or last '.'
        var slashIdx = qualifiedName.LastIndexOf('/');
        int splitIndex;
        if (slashIdx >= 0)
            splitIndex = slashIdx;
        else
            splitIndex = qualifiedName.LastIndexOf('.');

        if (splitIndex < 0)
            return [];

        var typeName = qualifiedName[..splitIndex];
        var methodName = qualifiedName[(splitIndex + 1)..];

        // Try to find the type, including generic type definitions (e.g. ConcurrentBag`1)
        var type = FindType(typeName);
        if (type is null)
            // Generic types are registered with backtick arity suffix — try `1 through `4
            for (var arity = 1; arity <= 4 && type is null; arity++)
                type = FindType($"{typeName}`{arity}");

        if (type is null)
            return [];

        var method = FindMethodIncludingInterfaces(type, methodName, flags);
        if (method is null)
            return [];

        var outParams = new List<OutParamInfo>();
        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
            if (parameters[i].IsOut)
            {
                var elemType = MapClrTypeToZType(parameters[i].ParameterType.GetElementType()!);
                outParams.Add(new OutParamInfo(i, elemType));
            }

        return outParams;
    }

    private ZType MapClrTypeWithGenerics(Type clrType, Dictionary<Type, ZType> genericMapping)
    {
        if (clrType.IsGenericParameter && genericMapping.TryGetValue(clrType, out var mapped))
            return mapped;
        return MapClrTypeToZType(clrType);
    }

    public ZType MethodInfoToZFuncType(MethodInfo method)
    {
        var paramTypes = method
            .GetParameters()
            .Select(p => MapClrTypeToZType(p.ParameterType))
            .ToList();
        var returnType = MapClrTypeToZType(method.ReturnType);
        return new ZType.ZFuncType(paramTypes, returnType);
    }

    /// <summary>
    ///     Returns true when a ZScheme function shape structurally matches a CLR delegate
    ///     type's Invoke signature (same arity, element types unify). The abstract bases
    ///     System.Delegate / System.MulticastDelegate have no Invoke method and are never
    ///     a structural match — this is what allows preferring a concrete delegate overload
    ///     (e.g. RequestDelegate) over the base Delegate overload.
    /// </summary>
    public bool FuncTypeMatchesDelegate(
        ZType.ZFuncType funcType,
        Type delegateClrType,
        SourceSpan span
    )
    {
        if (delegateClrType == typeof(Delegate) || delegateClrType == typeof(MulticastDelegate))
            return false;
        if (!typeof(Delegate).IsAssignableFrom(delegateClrType))
            return false;

        var invoke = delegateClrType.GetMethod("Invoke");
        if (invoke is null)
            return false;

        var invokeParams = invoke.GetParameters();
        if (invokeParams.Length != funcType.Params.Count)
            return false;

        var invokeZType = new ZType.ZFuncType(
            invokeParams.Select(p => MapClrTypeToZType(p.ParameterType)).ToList(),
            MapClrTypeToZType(invoke.ReturnType)
        );

        var scratchDiag = new DiagnosticBag();
        var scratchUnifier = new Unifier(new Substitution(), scratchDiag, _searchPaths);
        return scratchUnifier.Unify(funcType, invokeZType, span) && !scratchDiag.HasErrors;
    }

    /// <summary>
    ///     A candidate is "delegate-shape specific" when every argument that is a
    ///     function/delegate maps to a concrete (non-base-Delegate) delegate parameter
    ///     that matches the argument — structurally for ZFuncType args, nominally for
    ///     ZDelegateType args. Used to break overload ties in favor of the concrete
    ///     delegate overload.
    /// </summary>
    private bool IsDelegateShapeSpecific(
        MethodInfo candidate,
        ZType.ZFuncType resolvedFuncType,
        SourceSpan span
    )
    {
        var ps = candidate.GetParameters();
        var args = resolvedFuncType.Params;
        var sawDelegateArg = false;

        for (var i = 0; i < args.Count && i < ps.Length; i++)
        {
            var argT = args[i];
            if (argT is not (ZType.ZFuncType or ZType.ZDelegateType))
                continue;

            var paramClr = ps[i].ParameterType;
            // Argument is function-like but the parameter is not a delegate at all —
            // this candidate cannot be the delegate-specific match.
            if (!typeof(Delegate).IsAssignableFrom(paramClr))
                return false;

            sawDelegateArg = true;

            // The abstract base delegate types are never the specific match.
            if (paramClr == typeof(Delegate) || paramClr == typeof(MulticastDelegate))
                return false;

            switch (argT)
            {
                case ZType.ZFuncType ft when !FuncTypeMatchesDelegate(ft, paramClr, span):
                    return false;
                case ZType.ZDelegateType dt
                    when paramClr.FullName != dt.ClrTypeName && paramClr.Name != dt.ClrTypeName:
                    return false;
            }
        }

        return sawDelegateArg;
    }

    /// <summary>
    ///     Like MethodInfoToZFuncType, but auto-detects out parameters.
    ///     Out params are removed from the visible parameter list and appended to the return type
    ///     as a ValueTuple (original-return, out1, out2, ...).
    /// </summary>
    public (
        ZType FuncType,
        IReadOnlyList<OutParamInfo> OutParams
    ) MethodInfoToZFuncTypeWithOutParams(MethodInfo method)
    {
        var outParams = new List<OutParamInfo>();
        var visibleParamTypes = new List<ZType>();

        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.IsOut)
            {
                // Strip the ByRef wrapper to get the element type
                var elemType = MapClrTypeToZType(p.ParameterType.GetElementType()!);
                outParams.Add(new OutParamInfo(i, elemType));
            }
            else
            {
                visibleParamTypes.Add(MapClrTypeToZType(p.ParameterType));
            }
        }

        var returnType = MapClrTypeToZType(method.ReturnType);

        if (outParams.Count > 0)
        {
            // Return type becomes a ValueTuple: (original-return, out1, out2, ...)
            var tupleElements = new List<ZType> { returnType };
            tupleElements.AddRange(outParams.Select(op => op.ElementType));
            returnType = new ZType.ZNamedType("ValueTuple", tupleElements);
        }

        return (new ZType.ZFuncType(visibleParamTypes, returnType), outParams);
    }

    /// <summary>
    ///     Resolve a method by name, walking base interfaces when <paramref name="type"/>
    ///     is an interface. Reflection on an interface does not surface members inherited
    ///     from its base interfaces (e.g. <c>IDictionary&lt;,&gt;.TryGetValue</c> on
    ///     <c>IHeaderDictionary</c>), so a plain <c>GetMethod</c> returns null for them.
    /// </summary>
    internal static MethodInfo? FindMethodIncludingInterfaces(
        Type type,
        string methodName,
        BindingFlags flags
    )
    {
        foreach (var candidate in InterfaceClosure(type))
        {
            MethodInfo? method;
            try
            {
                method = candidate.GetMethod(methodName, flags);
            }
            catch (AmbiguousMatchException)
            {
                method = PickBestOverload(candidate, methodName, flags);
            }

            if (method is not null)
                return method;
        }

        return null;
    }

    /// <summary>
    ///     The type itself followed by its base interfaces (only when it is an interface).
    /// </summary>
    private static IEnumerable<Type> InterfaceClosure(Type type)
    {
        yield return type;
        if (type.IsInterface)
            foreach (var baseIface in type.GetInterfaces())
                yield return baseIface;
    }

    private static MethodInfo? PickBestOverload(Type type, string methodName, BindingFlags flags)
    {
        var candidates = type.GetMethods(flags).Where(m => m.Name == methodName).ToList();

        // Prefer overloads with out parameters (e.g., TryRemove(key, out value) over TryRemove(KeyValuePair))
        var withOut = candidates.FirstOrDefault(m => m.GetParameters().Any(p => p.IsOut));
        if (withOut is not null)
            return withOut;

        // Prefer string overload, then object (most general), then any single-param
        return candidates.FirstOrDefault(m =>
                m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(string)
            )
            ?? candidates.FirstOrDefault(m =>
                m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(object)
            )
            ?? candidates.FirstOrDefault(m => m.GetParameters().Length == 1)
            ?? candidates.FirstOrDefault();
    }

    /// <summary>
    ///     Eagerly load an assembly by simple name (from an <c>import-clr … :from</c>
    ///     hint) into the default load context. This makes its types visible to the
    ///     loaded-assembly scan in <see cref="FindType"/>, which is the only way to
    ///     resolve types whose namespace does not match their assembly file name
    ///     (e.g. <c>Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions</c>,
    ///     which ships in <c>Microsoft.AspNetCore.Routing.dll</c>). Idempotent.
    /// </summary>
    public void EnsureAssemblyLoaded(string assemblyName, SourceSpan span)
    {
        // Already loaded?
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            if (
                string.Equals(
                    loaded.GetName().Name,
                    assemblyName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return;

        // Try the normal resolver first (covers framework assemblies on the
        // trusted-platform-assembly list and the search-path Resolving handler).
        try
        {
            AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(assemblyName));
            return;
        }
        catch
        {
            // Fall through to an explicit file probe.
        }

        var probeDirs = new List<string> { AppDomain.CurrentDomain.BaseDirectory };
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        if (runtimeDir != AppDomain.CurrentDomain.BaseDirectory)
            probeDirs.Add(runtimeDir);
        probeDirs.AddRange(_searchPaths);

        foreach (var dir in probeDirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                continue;

            var candidate = Path.Combine(dir, assemblyName + ".dll");
            if (!File.Exists(candidate))
                continue;

            try
            {
                AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(candidate));
                return;
            }
            catch
            {
                // Try the next directory.
            }
        }

        _diagnostics.Error($"CLR assembly not found for ':from' hint: '{assemblyName}'", span);
    }

    public Type? FindType(string typeName)
    {
        // C#-style generic names (e.g. System.Func<int,int>) cannot be parsed by
        // Type.GetType/Assembly.GetType directly — convert them to the reflection
        // form (System.Func`2[System.Int32,System.Int32]) and search loaded assemblies.
        if (typeName.Contains('<'))
        {
            var reflectionName = AsmResolverTypeMapper.ConvertToReflectionTypeName(typeName);
            var generic = Type.GetType(reflectionName);
            if (generic is not null)
                return generic;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                generic = assembly.GetType(reflectionName);
                if (generic is not null)
                    return generic;
            }
        }

        // Try direct resolution
        var type = Type.GetType(typeName);
        if (type is not null)
            return type;

        // Search loaded assemblies
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(typeName);
            if (type is not null)
                return type;
        }

        var nsPrefix = typeName.Contains('.') ? typeName[..typeName.LastIndexOf('.')] : typeName;

        // Probe unloaded assemblies by namespace prefix in the base directory
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        type = ProbeDirectory(baseDir, typeName, nsPrefix);
        if (type is not null)
            return type;

        // Probe the .NET runtime directory (for framework assemblies like System.Net.Http)
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        if (runtimeDir != baseDir)
        {
            type = ProbeDirectory(runtimeDir, typeName, nsPrefix);
            if (type is not null)
                return type;
        }

        // Probe additional search paths
        foreach (var searchPath in _searchPaths)
        {
            if (!Directory.Exists(searchPath))
                continue;

            type = ProbeDirectory(searchPath, typeName, nsPrefix);
            if (type is not null)
                return type;
        }

        return null;
    }

    private static Type? ProbeDirectory(string directory, string typeName, string nsPrefix)
    {
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll"))
        {
            var fileName = Path.GetFileNameWithoutExtension(dll);
            if (
                !nsPrefix.StartsWith(fileName, StringComparison.OrdinalIgnoreCase)
                && !fileName.StartsWith(nsPrefix, StringComparison.OrdinalIgnoreCase)
            )
                continue;

            try
            {
                var fullPath = Path.GetFullPath(dll);
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
                var type = asm.GetType(typeName);
                if (type is not null)
                    return type;
            }
            catch
            {
                // Skip assemblies that fail to load
            }
        }

        return null;
    }

    /// <summary>
    ///     Metadata about a CLR out parameter: its original index in the method signature
    ///     and the element type (with the ByRef wrapper stripped).
    /// </summary>
    public record OutParamInfo(int OriginalIndex, ZType ElementType);
}
