using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Syntax;

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

    private List<CompiledModule>? TryLoadPrecompiledModules(string packageName, string version)
    {
        var package = _packageCache.TryLoad(packageName, version);
        return LoadModulesFromPackage(package);
    }

    private static List<CompiledModule>? LoadModulesFromPackage(PrecompiledPackage? package)
    {
        if (package is null)
            return null;

        var result = new List<CompiledModule>();
        foreach (var (moduleName, info) in package.Modules)
        {
            // Use type declarations from metadata (if available) instead of empty list
            var irDefs = info.TypeDeclarations ?? [];

            var compiled = new CompiledModule(
                info.Name,
                package.AssemblyPath,
                info.ExportedNames,
                info.ExportedTypes,
                info.ExportedClrImports,
                irDefs,
                info.ExportedClrNamespaces,
                info.ExportedMacros ?? new Dictionary<string, MacroDefinition>(),
                info.ExportedUnionCtors,
                info.ExportedRecordCtors,
                ExportedClassInterfaces: info.ExportedClassInterfaces,
                PrecompiledAssemblyPath: package.AssemblyPath
            );
            result.Add(compiled);
        }

        return result;
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

            foreach (var (moduleName, info) in package.Modules)
            {
                var irDefs = info.TypeDeclarations ?? [];

                var compiled = new CompiledModule(
                    info.Name,
                    package.AssemblyPath,
                    info.ExportedNames,
                    info.ExportedTypes,
                    info.ExportedClrImports,
                    irDefs,
                    info.ExportedClrNamespaces,
                    info.ExportedMacros ?? new Dictionary<string, MacroDefinition>(),
                    info.ExportedUnionCtors,
                    info.ExportedRecordCtors,
                    ExportedClassInterfaces: info.ExportedClassInterfaces,
                    PrecompiledAssemblyPath: package.AssemblyPath
                );
                result.Add(compiled);
            }
        }

        return (result, aliases);
    }
}
