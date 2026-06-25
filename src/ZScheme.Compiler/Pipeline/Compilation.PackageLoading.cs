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
        return LoadModulesFromPackage(package);
    }

    internal static List<CompiledModule>? LoadModulesFromPackage(PrecompiledPackage? package)
    {
        if (package is null)
            return null;
        Log.Debug(
            "LoadModulesFromPackage: loading {ModuleCount} modules from {AssemblyPath}",
            package.Modules.Count,
            package.AssemblyPath
        );

        var result = new List<CompiledModule>();
        foreach (var (_, info) in package.Modules)
        {
            // Use type declarations from metadata (if available) instead of empty list
            var irDefs = info.ExportedIrDefinitions;
            string? sourcePath = null;
            package.ModuleSourcePaths?.TryGetValue(info.Name, out sourcePath);

            var compiled = new CompiledModule(
                info.Name,
                package.AssemblyPath,
                info.ExportedNames,
                info.ExportedTypes,
                info.ExportedClrImports,
                irDefs,
                info.ExportedClrNamespaces,
                info.ExportedMacros,
                info.ExportedUnionCtors,
                info.ExportedRecordCtors,
                info.ExportedClassInterfaces,
                package.AssemblyPath,
                AllIrDefinitions: null,
                PrecompiledSourcePath: sourcePath,
                BuildNamespace: info.BuildNamespace,
                EmittedNames: info.EmittedNames,
                TypeEmittedNames: info.TypeEmittedNames
            );
            result.Add(compiled);
        }

        return result;
    }

    /// <summary>
    ///     Tries to load precompiled modules from explicit .dll paths in compiler options.
    /// </summary>
    private (
        List<CompiledModule> Modules,
        Dictionary<string, string> Aliases
    ) LoadExplicitPrecompiledPackages()
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
            Log.Debug("LoadExplicitPrecompiled: loading {DllPath}", dllPath);

            var json = File.ReadAllText(metadataPath);
            var package = MetadataSerializer.Deserialize(json, dllPath);
            if (package is null)
                continue;

            // Register module alias from package metadata (e.g., "zunit" → "zunit/zunit")
            if (package.ImportPrefix is not null && package.DefaultModule is not null)
                aliases[package.ImportPrefix] = $"{package.ImportPrefix}/{package.DefaultModule}";
            Log.Debug(
                "LoadExplicitPrecompiled: {DllPath} loaded {ModuleCount} modules, alias={Alias}",
                dllPath,
                package.Modules.Count,
                package.ImportPrefix ?? "(none)"
            );

            foreach (var (_, info) in package.Modules)
            {
                var irDefs = info.ExportedIrDefinitions;
                string? sourcePath = null;
                package.ModuleSourcePaths?.TryGetValue(info.Name, out sourcePath);

                var compiled = new CompiledModule(
                    info.Name,
                    package.AssemblyPath,
                    info.ExportedNames,
                    info.ExportedTypes,
                    info.ExportedClrImports,
                    irDefs,
                    info.ExportedClrNamespaces,
                    info.ExportedMacros,
                    info.ExportedUnionCtors,
                    info.ExportedRecordCtors,
                    info.ExportedClassInterfaces,
                    package.AssemblyPath,
                    AllIrDefinitions: null,
                    PrecompiledSourcePath: sourcePath,
                    BuildNamespace: info.BuildNamespace,
                    EmittedNames: info.EmittedNames,
                    TypeEmittedNames: info.TypeEmittedNames
                );
                result.Add(compiled);
            }
        }

        return (result, aliases);
    }
}
