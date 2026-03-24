using ZScript.Compiler.Diagnostics;

namespace ZScript.Compiler.Modules;

public sealed class ModuleResolver(DiagnosticBag diagnostics)
{
    private readonly Dictionary<string, string> _moduleAliases = new();
    private readonly Dictionary<string, List<string>> _packagePaths = new();
    private readonly List<string> _searchPaths = new();

    public IReadOnlyList<string> SearchPaths => _searchPaths;

    public void AddSearchPath(string path)
    {
        if (Directory.Exists(path))
            _searchPaths.Add(Path.GetFullPath(path));
    }

    public void AddPackagePath(string packageName, string path)
    {
        if (!Directory.Exists(path)) return;
        var fullPath = Path.GetFullPath(path);
        if (!_packagePaths.TryGetValue(packageName, out var paths))
        {
            paths = new List<string>();
            _packagePaths[packageName] = paths;
        }

        paths.Add(fullPath);
    }

    public void AddModuleAlias(string alias, string qualifiedName)
    {
        _moduleAliases[alias] = qualifiedName;
    }

    public string ResolveAlias(string moduleName)
    {
        return _moduleAliases.TryGetValue(moduleName, out var qualified) ? qualified : moduleName;
    }

    public (string Path, string Source)? Resolve(string moduleName)
    {
        // Resolve aliases (e.g., "zunit" → "zunit/zunit")
        if (_moduleAliases.TryGetValue(moduleName, out var aliased))
            moduleName = aliased;

        // Check for package-qualified names (e.g., "stdlib/option")
        var slashIndex = moduleName.IndexOf('/');
        if (slashIndex > 0)
        {
            var prefix = moduleName[..slashIndex];
            if (_packagePaths.TryGetValue(prefix, out var pkgPaths))
            {
                var rest = moduleName[(slashIndex + 1)..];
                var relativePath = rest.Replace('/', Path.DirectorySeparatorChar) + ".zs";
                foreach (var searchPath in pkgPaths)
                {
                    var fullPath = Path.Combine(searchPath, relativePath);
                    if (File.Exists(fullPath))
                        return (fullPath, File.ReadAllText(fullPath));
                }

                var searched = string.Join(", ", pkgPaths);
                diagnostics.Error($"Module not found: '{moduleName}' (searched: {searched})", SourceSpan.None);
                return null;
            }
        }

        // Fall back to unqualified search paths
        var unqualifiedPath = moduleName.Replace('/', Path.DirectorySeparatorChar) + ".zs";

        foreach (var searchPath in _searchPaths)
        {
            var fullPath = Path.Combine(searchPath, unqualifiedPath);
            if (File.Exists(fullPath))
                return (fullPath, File.ReadAllText(fullPath));
        }

        var allSearched = string.Join(", ", _searchPaths);
        diagnostics.Error($"Module not found: '{moduleName}' (searched: {allSearched})", SourceSpan.None);
        return null;
    }
}
