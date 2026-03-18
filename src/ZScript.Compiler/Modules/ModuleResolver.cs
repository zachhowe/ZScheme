namespace ZScript.Compiler.Modules;

using ZScript.Compiler.Diagnostics;

public sealed class ModuleResolver(DiagnosticBag diagnostics)
{
    private readonly List<string> _searchPaths = new();

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
        diagnostics.Error($"Module not found: '{moduleName}' (searched: {searched})", SourceSpan.None);
        return null;
    }
}
