namespace ZScript.Compiler.Codegen;

using System.Reflection;
using ZScript.Compiler.Diagnostics;

public sealed class ClrInterop
{
    private readonly DiagnosticBag _diagnostics;

    public ClrInterop(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Resolves "System.Math/Sqrt" to a MethodInfo.
    /// Format: TypeFullName/MethodName
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

        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        if (method is null)
        {
            // Try instance methods
            method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        }

        if (method is null)
        {
            _diagnostics.Error($"CLR method not found: '{methodName}' on type '{typeName}'", span);
            return null;
        }

        return method;
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

        return null;
    }
}
