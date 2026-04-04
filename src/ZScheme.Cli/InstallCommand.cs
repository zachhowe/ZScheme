using System.Diagnostics;
using Serilog;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Cli;

internal static class InstallCommand
{
    public static int Run(string[] args)
    {
        string? manifestPath = null;
        var packPackagePaths = new Dictionary<string, string>();
        var packModuleAliases = new Dictionary<string, string>();

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--manifest" or "-m" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--package-path" when i + 1 < args.Length:
                    var packResolved = CliHelpers.ResolvePackagePath(args[++i]);
                    if (packResolved is not null)
                    {
                        packPackagePaths[packResolved.Value.Prefix] = packResolved.Value.SourceDir;
                        if (packResolved.Value.DefaultModule is { } packDefMod)
                            packModuleAliases[packResolved.Value.Prefix] =
                                $"{packResolved.Value.Prefix}/{packDefMod}";
                    }

                    break;
            }

        // Find manifest if not specified
        if (manifestPath is null)
        {
            var candidates = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.zspkg");
            if (candidates.Length == 0)
            {
                Console.Error.WriteLine(
                    "No .zspkg manifest found in current directory. Use --manifest to specify one.");
                return 1;
            }

            if (candidates.Length > 1)
            {
                Console.Error.WriteLine("Multiple .zspkg files found. Use --manifest to specify one.");
                return 1;
            }

            manifestPath = candidates[0];
        }

        Log.Debug("install: manifest={ManifestPath}", manifestPath);

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Manifest not found: {manifestPath}");
            return 1;
        }

        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var diagnostics = new DiagnosticBag();

        // Parse manifest
        var manifestSource = File.ReadAllText(manifestPath);
        var parser = new ManifestParser(diagnostics);
        var manifest = parser.Parse(manifestSource, manifestPath);
        if (manifest is null)
        {
            foreach (var diag in diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        Log.Debug("install: parsed manifest name={Name}, version={Version}, nugetDeps={NuGetCount}",
            manifest.Name, manifest.Version, manifest.Dependencies.NuGet.Count);

        // Resolve NuGet dependencies
        var assemblySearchPaths = new List<string>();
        if (manifest.Dependencies.NuGet.Count > 0)
        {
            var nugetResolver = new NuGetResolver(diagnostics);
            var nugetOutputDir = nugetResolver.Resolve(manifest.Dependencies.NuGet);
            if (nugetOutputDir is null && diagnostics.HasErrors)
            {
                foreach (var diag in diagnostics.Diagnostics)
                    Console.Error.WriteLine(diag);
                return 1;
            }

            if (nugetOutputDir is not null)
            {
                assemblySearchPaths.Add(nugetOutputDir);
                Log.Debug("install: resolved NuGet dependencies to {OutputDir}", nugetOutputDir);
            }
        }

        // Resolve ZScheme dependencies from manifest
        foreach (var dep in manifest.Dependencies.ZScheme)
            if (dep.Source is ZSchemeDependencySource.Local local)
            {
                var depDir = Path.GetFullPath(Path.Combine(manifestDir, local.Path));
                var depResolved = CliHelpers.ResolvePackagePath(depDir);
                if (depResolved is not null)
                {
                    packPackagePaths.TryAdd(depResolved.Value.Prefix, depResolved.Value.SourceDir);
                    if (depResolved.Value.DefaultModule is { } defMod)
                        packModuleAliases.TryAdd(depResolved.Value.Prefix,
                            $"{depResolved.Value.Prefix}/{defMod}");
                }
            }

        // Add manifest-level ref paths for CLR assembly resolution
        foreach (var refPath in manifest.Build.RefPaths)
            assemblySearchPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

        var options = new CompilerOptions
        {
            AssemblySearchPaths = assemblySearchPaths,
            PackagePaths = packPackagePaths,
            ModuleAliases = packModuleAliases
        };

        // Compile as library
        var installSw = Stopwatch.StartNew();
        var libraryCompiler = new LibraryCompiler(diagnostics);
        var result = libraryCompiler.Compile(manifestDir, manifest, options);
        Log.Debug("install: library compilation completed in {ElapsedMs}ms, success={Success}",
            installSw.ElapsedMilliseconds, result is not null);
        if (result is null)
        {
            foreach (var diag in diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        // Store in cache
        var cacheManager = new PackageCacheManager();
        cacheManager.Store(manifest.Name, manifest.Version, result.AssemblyBytes, result.Modules,
            manifest.ImportPrefix, manifest.DefaultModule);

        var cachePath = Path.Combine(ZSchemePaths.GetPackageCacheRoot(), manifest.Name, manifest.Version);
        Log.Debug("install: stored package {Name}@{Version} in cache at {CachePath}", manifest.Name, manifest.Version,
            cachePath);
        Console.WriteLine($"Package '{manifest.Name}' v{manifest.Version} cached at: {cachePath}");
        return 0;
    }
}
