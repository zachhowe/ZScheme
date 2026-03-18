namespace ZScript.Compiler.Modules;

using ZScript.Compiler.Diagnostics;

public sealed class ModuleResolver
{
    private readonly string _basePath;
    private readonly DiagnosticBag _diagnostics;

    public ModuleResolver(string basePath, DiagnosticBag diagnostics)
    {
        _basePath = basePath;
        _diagnostics = diagnostics;
    }

    public string? Resolve(string moduleName)
    {
        // Module name uses / as separator: "math/vector" -> "math/vector.zs"
        var relativePath = moduleName.Replace('/', Path.DirectorySeparatorChar) + ".zs";
        var fullPath = Path.Combine(_basePath, relativePath);

        if (File.Exists(fullPath))
            return fullPath;

        _diagnostics.Error($"Module not found: '{moduleName}' (looked at {fullPath})", SourceSpan.None);
        return null;
    }

    public string ReadSource(string filePath) => File.ReadAllText(filePath);
}
