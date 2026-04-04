using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Modules;

namespace ZScheme.Compiler.Pipeline;

public sealed partial class Compilation
{
    /// <summary>
    ///     Attempts to load modules from a precompiled package in the cache.
    ///     Returns CompiledModule records with type declarations from metadata
    ///     and PrecompiledAssemblyPath set. Function IR lives in the .dll.
    /// </summary>
    private List<CompiledModule>? TryLoadPrecompiledModules(string packageName)
    {
        var package = _packageCache.TryLoadLatest(packageName);
        return package?.Modules.Values.ToList();
    }

    private List<CompiledModule>? TryLoadPrecompiledModules(string packageName, string version)
    {
        var package = _packageCache.TryLoad(packageName, version);
        return package?.Modules.Values.ToList();
    }

    /// <summary>
    ///     Tries to load precompiled modules from explicit .dll paths in compiler options.
    /// </summary>
    private (List<CompiledModule> Modules, Dictionary<string, string> Aliases) LoadExplicitPrecompiledPackages()
    {
        var result = new List<CompiledModule>();
        var aliases = new Dictionary<string, string>();
        foreach (var dllPath in _options.PrecompiledPackagePaths)
        {
            if (!File.Exists(dllPath))
                continue;

            var metadataPath = Path.ChangeExtension(dllPath, ".metadata.json");
            if (!File.Exists(metadataPath))
                continue;

            var json = File.ReadAllText(metadataPath);
            var package = MetadataSerializer.Deserialize(json, dllPath);
            if (package is null)
                continue;

            // Register module alias from package metadata (e.g., "zunit" → "zunit/zunit")
            if (package.ImportPrefix is not null && package.DefaultModule is not null)
                aliases[package.ImportPrefix] = $"{package.ImportPrefix}/{package.DefaultModule}";

            result.AddRange(package.Modules.Values);
        }

        return (result, aliases);
    }
}
