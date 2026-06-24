using System.Diagnostics;
using Serilog;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Package;

public sealed class PackageBuilder(DiagnosticBag diagnostics)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PackageBuilder>();

    public CompilationResult? Build(string manifestPath, CompilerOptions? cliOverrides = null)
    {
        Log.Debug("PackageBuilder: building from {ManifestPath}", manifestPath);

        if (!File.Exists(manifestPath))
        {
            diagnostics.Error($"Manifest not found: {manifestPath}", SourceSpan.None);
            return null;
        }

        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var manifestSource = File.ReadAllText(manifestPath);

        // 1. Parse manifest
        var parser = new ManifestParser(diagnostics);
        var manifest = parser.Parse(manifestSource, manifestPath);
        if (manifest is null)
            return null;

        Log.Debug(
            "PackageBuilder: manifest parsed, name={Name}, entry={Entry}",
            manifest.Name,
            manifest.Entry
        );

        // 2. Resolve ZScheme dependencies, prefix-aware. Mirrors PackageTester so that
        //    `build -m` and `test -m` resolve local deps identically: a dependency package's
        //    own manifest supplies its import-prefix and source dir, letting the consumer
        //    import the dependency's prefixed modules (e.g. aspnet/app) from source. Its
        //    framework / NuGet / ref-path inputs are propagated too so the dependency's
        //    sources resolve their CLR types when recompiled inside this build.
        var assemblySearchPaths = new List<string>();
        var moduleSearchPaths = new List<string>();
        var packagePaths = new Dictionary<string, string>();
        var moduleAliases = new Dictionary<string, string>();
        var nugetDeps = new List<NuGetDependency>(manifest.Dependencies.NuGet);
        // Shared-framework ids (consumer + transitive) for an executable's runtimeconfig.json.
        var frameworkIds = new List<string>();

        if (manifest.Dependencies.ZScheme.Count > 0)
        {
            // Walk the full transitive closure so a dep-of-a-dep's prefixed modules resolve
            // without the consumer re-declaring them (e.g. depending on aspnet → stdlib/...).
            var closure = PackageDependencyResolver.ResolveTransitiveClosure(
                manifest.Dependencies.ZScheme,
                manifestDir,
                diagnostics
            );
            if (diagnostics.HasErrors)
                return null;

            moduleSearchPaths.AddRange(closure.ModuleSearchPaths);
            foreach (var (prefix, path) in closure.PackagePaths)
                packagePaths[prefix] = path;
            foreach (var (prefix, alias) in closure.ModuleAliases)
                moduleAliases[prefix] = alias;
            assemblySearchPaths.AddRange(
                FrameworkResolver.Resolve(closure.Frameworks, diagnostics)
            );
            assemblySearchPaths.AddRange(closure.RefPaths);
            nugetDeps.AddRange(closure.NuGet);
            frameworkIds.AddRange(closure.Frameworks.Select(f => f.Id));

            if (diagnostics.HasErrors)
                return null;

            Log.Debug(
                "PackageBuilder: ZScheme dependencies resolved (transitive), {PathCount} module search paths",
                moduleSearchPaths.Count
            );
        }

        // 3. Resolve the consumer's own shared-framework references (e.g.
        //    Microsoft.AspNetCore.App) so entry + dependency sources can resolve framework types.
        assemblySearchPaths.AddRange(
            FrameworkResolver.Resolve(manifest.Dependencies.Frameworks, diagnostics)
        );
        frameworkIds.AddRange(manifest.Dependencies.Frameworks.Select(f => f.Id));
        if (diagnostics.HasErrors)
            return null;

        // 4. Resolve NuGet dependencies (consumer + transitive from dependency manifests).
        if (nugetDeps.Count > 0)
        {
            var nugetResolver = new NuGetResolver(diagnostics);
            var nugetOutputDir = nugetResolver.Resolve(nugetDeps);
            if (nugetOutputDir is null && diagnostics.HasErrors)
                return null;
            if (nugetOutputDir is not null)
            {
                assemblySearchPaths.Add(nugetOutputDir);
                Log.Debug(
                    "PackageBuilder: NuGet dependencies resolved to {OutputDir}",
                    nugetOutputDir
                );
            }
        }

        // 5. Merge manifest scalar BuildConfig with CLI overrides, then layer collections
        //    auto-resolved → CLI so explicit CLI flags win.
        var options = MergeOptions(manifest.Build, cliOverrides);
        options.FrameworkReferences = frameworkIds.Distinct().ToList();

        // Consumer's own manifest-level ref paths (relative to the manifest dir).
        if (manifest.Build.Main is { } mainBuild)
            foreach (var refPath in mainBuild.RefPaths)
                assemblySearchPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

        AddDistinct(options.AssemblySearchPaths, assemblySearchPaths);
        AddDistinct(options.ModuleSearchPaths, moduleSearchPaths);
        foreach (var (prefix, path) in packagePaths)
            options.PackagePaths[prefix] = path;
        foreach (var (prefix, alias) in moduleAliases)
            options.ModuleAliases[prefix] = alias;

        if (cliOverrides is not null)
        {
            AddDistinct(options.AssemblySearchPaths, cliOverrides.AssemblySearchPaths);
            AddDistinct(options.ModuleSearchPaths, cliOverrides.ModuleSearchPaths);
            foreach (var (prefix, path) in cliOverrides.PackagePaths)
                options.PackagePaths[prefix] = path;
            foreach (var (prefix, alias) in cliOverrides.ModuleAliases)
                options.ModuleAliases[prefix] = alias;
            AddDistinct(options.PrecompiledPackagePaths, cliOverrides.PrecompiledPackagePaths);
        }

        // 5. Read entry file and compile
        if (manifest.Entry is null)
        {
            diagnostics.Error("No entry file specified; nothing to compile.", manifest.Span);
            return null;
        }

        var entryPath = Path.GetFullPath(Path.Combine(manifestDir, manifest.Entry));
        if (!File.Exists(entryPath))
        {
            diagnostics.Error($"Entry file not found: {entryPath}", manifest.Span);
            return null;
        }

        var source = File.ReadAllText(entryPath);
        var sw = Stopwatch.StartNew();
        var compilation = new Compilation(options);
        var result = compilation.Compile(source, entryPath);
        Log.Debug("PackageBuilder: compilation completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        return result;
    }

    private static CompilerOptions MergeOptions(
        BuildConfig buildConfig,
        CompilerOptions? cliOverrides
    )
    {
        var options = new CompilerOptions();

        // Start with manifest defaults (main build config)
        if (buildConfig.Main is { } main)
        {
            if (main.OutputPath is not null)
                options.OutputPath = main.OutputPath;
            if (main.Backend is not null)
                options.OutputMode = main.Backend.Value;
            if (main.Namespace is not null)
                options.Namespace = main.Namespace;
        }

        // CLI overrides win
        if (cliOverrides is null)
            return options;

        if (cliOverrides.OutputPath != "output")
            options.OutputPath = cliOverrides.OutputPath;
        if (cliOverrides.OutputMode != OutputMode.CSharp)
            options.OutputMode = cliOverrides.OutputMode;
        if (cliOverrides.Namespace != "ZSchemeGenerated")
            options.Namespace = cliOverrides.Namespace;

        // Collection merging (assembly/module search paths, package paths, aliases,
        // precompiled paths) is handled in Build() so auto-resolved dependency inputs and
        // CLI overrides are layered in a single, well-defined order.
        return options;
    }

    /// <summary>
    ///     Appends <paramref name="additions" /> to <paramref name="target" />, skipping
    ///     entries already present (case-insensitive, treating values as file-system paths).
    /// </summary>
    private static void AddDistinct(List<string> target, IEnumerable<string> additions)
    {
        var seen = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
        foreach (var item in additions)
            if (seen.Add(item))
                target.Add(item);
    }
}
