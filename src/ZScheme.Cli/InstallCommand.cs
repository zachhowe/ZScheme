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
                    "No .zspkg manifest found in current directory. Use --manifest to specify one."
                );
                return 1;
            }

            if (candidates.Length > 1)
            {
                Console.Error.WriteLine(
                    "Multiple .zspkg files found. Use --manifest to specify one."
                );
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

        Log.Debug(
            "install: parsed manifest name={Name}, version={Version}, nugetDeps={NuGetCount}",
            manifest.Name,
            manifest.Version,
            manifest.Dependencies.NuGet.Count
        );

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
                        packModuleAliases.TryAdd(
                            depResolved.Value.Prefix,
                            $"{depResolved.Value.Prefix}/{defMod}"
                        );
                }
            }

        // Build any local subproject whose build output a ref path points into (e.g. a
        // `bridge/` C# project referenced as `bridge/bin/Release/net10.0`), so consumers
        // don't have to build it by hand before installing.
        if (manifest.Build.Main is { } bridgeBuild)
            foreach (var refPath in bridgeBuild.RefPaths)
            {
                var projectDir = FindReferencedProjectDir(manifestDir, refPath);
                if (projectDir is null)
                    continue;
                var csproj = Directory.EnumerateFiles(projectDir, "*.csproj").FirstOrDefault();
                if (csproj is null)
                    continue;
                Console.WriteLine($"Building referenced project: {Path.GetFileName(csproj)}");
                if (!RunDotnetBuild(projectDir))
                {
                    Console.Error.WriteLine($"Failed to build referenced project: {csproj}");
                    return 1;
                }
            }

        // Add manifest-level ref paths for CLR assembly resolution (main build config)
        if (manifest.Build.Main is { } mainBuild)
            foreach (var refPath in mainBuild.RefPaths)
                assemblySearchPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

        // Add shared-framework directories (e.g. Microsoft.AspNetCore.App) so the
        // ZScheme compiler can resolve types from declared (framework ...) deps.
        assemblySearchPaths.AddRange(
            CliHelpers.ResolveFrameworkRefDirs(manifest.Dependencies.Frameworks, diagnostics)
        );
        if (diagnostics.HasErrors)
        {
            foreach (var diag in diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        var options = new CompilerOptions
        {
            AssemblySearchPaths = assemblySearchPaths,
            PackagePaths = packPackagePaths,
            ModuleAliases = packModuleAliases,
        };

        // Compile as library
        var installSw = Stopwatch.StartNew();
        var libraryCompiler = new LibraryCompiler(diagnostics);
        var result = libraryCompiler.Compile(manifestDir, manifest, options);
        Log.Debug(
            "install: library compilation completed in {ElapsedMs}ms, success={Success}",
            installSw.ElapsedMilliseconds,
            result is not null
        );
        if (result is null)
        {
            foreach (var diag in diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        // Store in cache
        var cacheManager = new PackageCacheManager();
        try
        {
            cacheManager.Store(
                manifest.Name,
                manifest.Version,
                result.AssemblyBytes,
                result.Modules,
                manifest.ImportPrefix,
                manifest.DefaultModule,
                dependencies: PackageDependencyResolver.ResolveDependencyIdentities(
                    manifest,
                    manifestDir
                ),
                inputFingerprint: PackageFingerprint.Compute(manifestDir, manifest)
            );
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }

        var cachePath = Path.Combine(
            ZSchemePaths.GetPackageCacheRoot(),
            manifest.Name,
            manifest.Version
        );
        Log.Debug(
            "install: stored package {Name}@{Version} in cache at {CachePath}",
            manifest.Name,
            manifest.Version,
            cachePath
        );
        Console.WriteLine($"Package '{manifest.Name}' v{manifest.Version} cached at: {cachePath}");
        return 0;
    }

    // Given a ref path like "bridge/bin/Release/net10.0", return the directory of the
    // project that produces it ("bridge"), or null if the ref isn't a build output of a
    // local subproject (no "bin" segment, or the directory doesn't exist).
    private static string? FindReferencedProjectDir(string manifestDir, string refPath)
    {
        var parts = refPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var binIdx = Array.FindIndex(
            parts,
            p => p.Equals("bin", StringComparison.OrdinalIgnoreCase)
        );
        if (binIdx <= 0)
            return null;
        var projDir = Path.GetFullPath(Path.Combine(manifestDir, Path.Combine(parts[..binIdx])));
        return Directory.Exists(projDir) ? projDir : null;
    }

    // Run `dotnet build <projectDir> -c Release`, streaming output only on failure.
    private static bool RunDotnetBuild(string projectDir)
    {
        var psi = new ProcessStartInfo("dotnet", $"build \"{projectDir}\" -c Release --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi);
        if (proc is null)
            return false;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode == 0)
            return true;
        Console.Error.WriteLine(stdout);
        Console.Error.WriteLine(stderr);
        return false;
    }
}
