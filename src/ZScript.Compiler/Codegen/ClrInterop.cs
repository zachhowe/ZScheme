namespace ZScript.Compiler.Codegen;

using System.Reflection;
using ZScript.Compiler.Diagnostics;

public sealed class ClrInterop(DiagnosticBag diagnostics)
{
    /// <summary>
    /// Resolves "System.Math/Sqrt" to a MethodInfo.
    /// Format: TypeFullName/MethodName
    /// </summary>
    public MethodInfo? Resolve(string qualifiedName, SourceSpan span)
    {
        var slashIndex = qualifiedName.LastIndexOf('/');
        if (slashIndex < 0)
        {
            diagnostics.Error($"Invalid CLR reference: '{qualifiedName}'. Expected Type/Method format.", span);
            return null;
        }

        var typeName = qualifiedName[..slashIndex];
        var methodName = qualifiedName[(slashIndex + 1)..];

        var type = FindType(typeName);
        if (type is null)
        {
            diagnostics.Error($"CLR type not found: '{typeName}'", span);
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
        {
            try
            {
                method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (AmbiguousMatchException)
            {
                method = PickBestOverload(type, methodName, BindingFlags.Public | BindingFlags.Instance);
            }
        }

        if (method is null)
        {
            diagnostics.Error($"CLR method not found: '{methodName}' on type '{typeName}'", span);
            return null;
        }

        return method;
    }

    public static Types.ZType MapClrTypeToZType(Type clrType)
    {
        if (clrType == typeof(int)) return Types.ZType.Int;
        if (clrType == typeof(long)) return Types.ZType.Long;
        if (clrType == typeof(float)) return Types.ZType.Float;
        if (clrType == typeof(double)) return Types.ZType.Double;
        if (clrType == typeof(byte)) return Types.ZType.Byte;
        if (clrType == typeof(char)) return Types.ZType.Char;
        if (clrType == typeof(bool)) return Types.ZType.Bool;
        if (clrType == typeof(string)) return Types.ZType.String;
        if (clrType == typeof(void)) return Types.ZType.Unit;
        return new Types.ZType.ZNamedType(clrType.FullName ?? clrType.Name, []);
    }

    public static Types.ZType MethodInfoToZFuncType(MethodInfo method)
    {
        var paramTypes = method.GetParameters()
            .Select(p => MapClrTypeToZType(p.ParameterType))
            .ToList();
        var returnType = MapClrTypeToZType(method.ReturnType);
        return new Types.ZType.ZFuncType(paramTypes, returnType);
    }

    private static MethodInfo? PickBestOverload(Type type, string methodName, BindingFlags flags)
    {
        var candidates = type.GetMethods(flags)
            .Where(m => m.Name == methodName).ToList();

        // Prefer string overload, then object (most general), then any single-param
        return candidates.FirstOrDefault(m =>
                   m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
               ?? candidates.FirstOrDefault(m =>
                   m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(object))
               ?? candidates.FirstOrDefault(m => m.GetParameters().Length == 1)
               ?? candidates.FirstOrDefault();
    }

    private static Type? FindType(string typeName)
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

        // Probe unloaded assemblies by namespace prefix in the base directory
        // This handles cases where a referenced assembly hasn't been loaded yet
        // (e.g., ZScript.ZUnit referenced by a test project but not yet triggered by the JIT)
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var nsPrefix = typeName.Contains('.')
            ? typeName[..typeName.LastIndexOf('.')]
            : typeName;

        foreach (var dll in Directory.EnumerateFiles(baseDir, "*.dll"))
        {
            var fileName = Path.GetFileNameWithoutExtension(dll);
            if (!nsPrefix.StartsWith(fileName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                type = asm.GetType(typeName);
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
}
