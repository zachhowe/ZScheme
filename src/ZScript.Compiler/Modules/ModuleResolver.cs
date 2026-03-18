namespace ZScript.Compiler.Modules;

using ZScript.Compiler.Diagnostics;

public sealed class ModuleResolver
{
    private readonly List<string> _searchPaths = new();
    private readonly DiagnosticBag _diagnostics;

    public ModuleResolver(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public void AddSearchPath(string path)
    {
        if (Directory.Exists(path))
            _searchPaths.Add(Path.GetFullPath(path));
    }

    public (string Path, string Source)? Resolve(string moduleName)
    {
        var relativePath = moduleName.Replace('/', Path.DirectorySeparatorChar) + ".zs";

        foreach (var searchPath in _searchPaths)
        {
            var fullPath = Path.Combine(searchPath, relativePath);
            if (File.Exists(fullPath))
                return (fullPath, File.ReadAllText(fullPath));
        }

        var searched = string.Join(", ", _searchPaths);
        _diagnostics.Error($"Module not found: '{moduleName}' (searched: {searched})", SourceSpan.None);
        return null;
    }
}
