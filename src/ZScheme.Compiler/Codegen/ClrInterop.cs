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

    public ClrInterop(DiagnosticBag diagnostics, IReadOnlyList<string>? assemblySearchPaths = null)
    {
        _diagnostics = diagnostics;
        _searchPaths = assemblySearchPaths ?? [];

        // Register an assembly resolution handler so that transitive dependencies of
        // assemblies loaded from search paths can be found.
        _resolveHandler = (context, assemblyName) =>
        {
            var simpleName = assemblyName.Name;
            if (simpleName is null) return null;

            foreach (var searchPath in _searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;

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
            _diagnostics.Error($"Invalid CLR reference: '{qualifiedName}'. Expected Type/Method format.", span);
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
                method = PickBestOverload(type, methodName, BindingFlags.Public | BindingFlags.Instance);
            }

        if (method is null)
        {
            _diagnostics.Error($"CLR method not found: '{methodName}' on type '{typeName}'", span);
            return null;
        }

        return method;
    }

    public static ZType MapClrTypeToZType(Type clrType)
    {
        if (clrType == typeof(int)) return ZType.Int;
        if (clrType == typeof(long)) return ZType.Long;
        if (clrType == typeof(float)) return ZType.Float;
        if (clrType == typeof(double)) return ZType.Double;
        if (clrType == typeof(byte)) return ZType.Byte;
        if (clrType == typeof(char)) return ZType.Char;
        if (clrType == typeof(bool)) return ZType.Bool;
        if (clrType == typeof(string)) return ZType.String;
        if (clrType == typeof(void)) return ZType.Unit;
        if (clrType.IsArray)
            return new ZType.ZNamedType("Mutable-Array", [MapClrTypeToZType(clrType.GetElementType()!)]);
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(List<>))
            return new ZType.ZNamedType("Mutable-TreeList", [MapClrTypeToZType(clrType.GetGenericArguments()[0])]);
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = clrType.GetGenericArguments();
            return new ZType.ZNamedType("Mutable-Map", [MapClrTypeToZType(args[0]), MapClrTypeToZType(args[1])]);
        }

        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(System.Collections.Concurrent.ConcurrentBag<>))
            return new ZType.ZNamedType("Concurrent-Bag", [MapClrTypeToZType(clrType.GetGenericArguments()[0])]);
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(System.Collections.Concurrent.ConcurrentQueue<>))
            return new ZType.ZNamedType("Concurrent-Queue", [MapClrTypeToZType(clrType.GetGenericArguments()[0])]);
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(System.Collections.Concurrent.ConcurrentStack<>))
            return new ZType.ZNamedType("Concurrent-Stack", [MapClrTypeToZType(clrType.GetGenericArguments()[0])]);
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(System.Collections.Concurrent.ConcurrentDictionary<,>))
        {
            var args = clrType.GetGenericArguments();
            return new ZType.ZNamedType("Concurrent-Dictionary", [MapClrTypeToZType(args[0]), MapClrTypeToZType(args[1])]);
        }

        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(Nullable<>))
            return new ZType.ZNullableType(MapClrTypeToZType(clrType.GetGenericArguments()[0]));
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(Task<>))
            return new ZType.ZNamedType("Task", [MapClrTypeToZType(clrType.GetGenericArguments()[0])]);
        return new ZType.ZNamedType(clrType.FullName ?? clrType.Name, []);
    }

    public MethodInfo? ResolveGeneric(string qualifiedName, int genericArity, SourceSpan span)
    {
        var slashIndex = qualifiedName.LastIndexOf('/');
        if (slashIndex < 0)
        {
            _diagnostics.Error($"Invalid CLR reference: '{qualifiedName}'. Expected Type/Method format.", span);
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
            .Where(m => m.Name == methodName
                        && m.IsGenericMethodDefinition
                        && m.GetGenericArguments().Length == genericArity)
            .ToList();

        if (candidates.Count == 0)
        {
            _diagnostics.Error(
                $"No generic method '{methodName}' with {genericArity} type parameter(s) on '{typeName}'",
                span);
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

    public static ZType GenericMethodInfoToZFuncType(MethodInfo method, IReadOnlyList<int> typeVarIds)
    {
        var genericArgs = method.GetGenericArguments();
        var mapping = new Dictionary<Type, ZType>();
        for (var i = 0; i < genericArgs.Length; i++)
            mapping[genericArgs[i]] = new ZType.ZTypeVar(typeVarIds[i]);

        var paramTypes = method.GetParameters()
            .Select(p => MapClrTypeWithGenerics(p.ParameterType, mapping))
            .ToList();
        var returnType = MapClrTypeWithGenerics(method.ReturnType, mapping);
        return new ZType.ZFuncType(paramTypes, returnType);
    }

    /// <summary>
    ///     Resolves an instance method from its qualified name (Type.Method or Type/Method)
    ///     and returns out-parameter metadata, if any.
    /// </summary>
    public IReadOnlyList<OutParamInfo> DetectOutParams(string qualifiedName, SourceSpan span,
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance)
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
        {
            // Generic types are registered with backtick arity suffix — try `1 through `4
            for (var arity = 1; arity <= 4 && type is null; arity++)
                type = FindType($"{typeName}`{arity}");
        }

        if (type is null)
            return [];

        MethodInfo? method;
        try
        {
            method = type.GetMethod(methodName, flags);
        }
        catch (AmbiguousMatchException)
        {
            method = PickBestOverload(type, methodName, flags);
        }

        if (method is null)
            return [];

        var outParams = new List<OutParamInfo>();
        var parameters = method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].IsOut)
            {
                var elemType = MapClrTypeToZType(parameters[i].ParameterType.GetElementType()!);
                outParams.Add(new OutParamInfo(i, elemType));
            }
        }

        return outParams;
    }

    private static ZType MapClrTypeWithGenerics(Type clrType, Dictionary<Type, ZType> genericMapping)
    {
        if (clrType.IsGenericParameter && genericMapping.TryGetValue(clrType, out var mapped))
            return mapped;
        return MapClrTypeToZType(clrType);
    }

    public static ZType MethodInfoToZFuncType(MethodInfo method)
    {
        var paramTypes = method.GetParameters()
            .Select(p => MapClrTypeToZType(p.ParameterType))
            .ToList();
        var returnType = MapClrTypeToZType(method.ReturnType);
        return new ZType.ZFuncType(paramTypes, returnType);
    }

    /// <summary>
    ///     Like MethodInfoToZFuncType, but auto-detects out parameters.
    ///     Out params are removed from the visible parameter list and appended to the return type
    ///     as a ValueTuple (original-return, out1, out2, ...).
    /// </summary>
    public static (ZType FuncType, IReadOnlyList<OutParamInfo> OutParams) MethodInfoToZFuncTypeWithOutParams(
        MethodInfo method)
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

    private static MethodInfo? PickBestOverload(Type type, string methodName, BindingFlags flags)
    {
        var candidates = type.GetMethods(flags)
            .Where(m => m.Name == methodName).ToList();

        // Prefer overloads with out parameters (e.g., TryRemove(key, out value) over TryRemove(KeyValuePair))
        var withOut = candidates.FirstOrDefault(m =>
            m.GetParameters().Any(p => p.IsOut));
        if (withOut is not null)
            return withOut;

        // Prefer string overload, then object (most general), then any single-param
        return candidates.FirstOrDefault(m =>
                   m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
               ?? candidates.FirstOrDefault(m =>
                   m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(object))
               ?? candidates.FirstOrDefault(m => m.GetParameters().Length == 1)
               ?? candidates.FirstOrDefault();
    }

    public Type? FindType(string typeName)
    {
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

        var nsPrefix = typeName.Contains('.')
            ? typeName[..typeName.LastIndexOf('.')]
            : typeName;

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
            if (!nsPrefix.StartsWith(fileName, StringComparison.OrdinalIgnoreCase)
                && !fileName.StartsWith(nsPrefix, StringComparison.OrdinalIgnoreCase))
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
