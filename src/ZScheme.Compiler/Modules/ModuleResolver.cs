using Serilog;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Modules;

public sealed class ModuleResolver(DiagnosticBag diagnostics)
{
    private readonly Dictionary<string, string> _moduleAliases = new();
    private readonly Dictionary<string, List<string>> _packagePaths = new();
    private readonly List<string> _searchPaths = new();

    public IReadOnlyList<string> SearchPaths => _searchPaths;

    public void AddSearchPath(string path)
    {
        if (Directory.Exists(path))
        {
            var fullPath = Path.GetFullPath(path);
            _searchPaths.Add(fullPath);
            Log.Debug("ModuleResolver: added search path {Path}", fullPath);
        }
        else
        {
            Log.Debug("ModuleResolver: skipped search path {Path} (directory not found)", path);
        }
    }

    public void AddPackagePath(string packageName, string path)
    {
        if (!Directory.Exists(path))
        {
            Log.Debug("ModuleResolver: skipped package path {PackageName}={Path} (directory not found)", packageName,
                path);
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!_packagePaths.TryGetValue(packageName, out var paths))
        {
            paths = new List<string>();
            _packagePaths[packageName] = paths;
        }

        paths.Add(fullPath);
        Log.Debug("ModuleResolver: registered package {PackageName} at {Path}", packageName, fullPath);
    }

    public void AddModuleAlias(string alias, string qualifiedName)
    {
        _moduleAliases[alias] = qualifiedName;
        Log.Debug("ModuleResolver: alias {Alias} -> {QualifiedName}", alias, qualifiedName);
    }

    public string ResolveAlias(string moduleName)
    {
        return _moduleAliases.TryGetValue(moduleName, out var qualified) ? qualified : moduleName;
    }

    public (string Path, string Source)? Resolve(string moduleName, SourceSpan span)
    {
        // Resolve aliases (e.g., "zunit" → "zunit/zunit")
        if (_moduleAliases.TryGetValue(moduleName, out var aliased))
        {
            Log.Debug("ModuleResolver: alias {OriginalName} -> {AliasedName}", moduleName, aliased);
            moduleName = aliased;
        }

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
                    {
                        Log.Debug("ModuleResolver: resolved {ModuleName} -> {Path}", moduleName, fullPath);
                        return (fullPath, File.ReadAllText(fullPath));
                    }
                }

                var searched = string.Join(", ", pkgPaths);
                diagnostics.Error($"Module not found: '{moduleName}' (searched: {searched})", span);
                return null;
            }
        }

        // Fall back to unqualified search paths
        var unqualifiedPath = moduleName.Replace('/', Path.DirectorySeparatorChar) + ".zs";

        foreach (var searchPath in _searchPaths)
        {
            var fullPath = Path.Combine(searchPath, unqualifiedPath);
            if (File.Exists(fullPath))
            {
                Log.Debug("ModuleResolver: resolved {ModuleName} -> {Path}", moduleName, fullPath);
                return (fullPath, File.ReadAllText(fullPath));
            }
        }

        var allSearched = string.Join(", ", _searchPaths);
        diagnostics.Error($"Module not found: '{moduleName}' (searched: {allSearched})", span);
        return null;
    }
}
