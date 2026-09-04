using Serilog;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Package;

/// <summary>
///     Everything resolving a package's manifest yields for a compile: where to find the
///     assemblies and modules its sources import, and the shared-framework ids a generated
///     executable needs in its runtimeconfig.
/// </summary>
public sealed record ResolvedPackageInputs(
    IReadOnlyList<string> AssemblySearchPaths,
    IReadOnlyList<string> ModuleSearchPaths,
    IReadOnlyDictionary<string, string> PackagePaths,
    IReadOnlyDictionary<string, string> ModuleAliases,
    IReadOnlyList<string> FrameworkIds
)
{
    /// <summary>
    ///     Dependency assemblies to reference instead of compiling from source. Empty unless the
    ///     caller asked for precompiled dependencies.
    /// </summary>
    public IReadOnlyList<string> PrecompiledPackagePaths { get; init; } = [];
}

/// <summary>
///     Turns a parsed <see cref="PackageManifest" /> into the search paths and package
///     mappings its sources need to compile. Every entry point that compiles a package —
///     <see cref="PackageBuilder" />, <see cref="LibraryCompiler.CompileFromManifest" />,
///     and in-process hosts — resolves through here, so they agree on what a manifest means.
/// </summary>
public static class PackageOptionsBuilder
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(PackageOptionsBuilder));

    /// <summary>
    ///     Resolves the manifest's ZScheme dependency closure, shared frameworks, NuGet
    ///     packages, and its own <c>(ref ...)</c> paths. Returns <c>null</c> if anything
    ///     fails, with the reason in <paramref name="diagnostics" />.
    /// </summary>
    /// <param name="resolveNuGetDependencies">
    ///     When <c>false</c>, declared NuGet packages are not downloaded or added to the
    ///     assembly search paths. In-process hosts want this: their own output directory
    ///     already carries the full transitive package graph, and a second copy of an
    ///     in-box assembly on the search path would load a duplicate into the default
    ///     load context and split that type's identity.
    /// </param>
    /// <param name="preferPrecompiledDependencies">
    ///     Reference a dependency's built assembly rather than compiling its sources into this
    ///     build. Off by default so a caller opts in explicitly: the language server resolves
    ///     go-to-definition into dependency sources and wants them compiled, and its answers should
    ///     not become as stale as the last build of a package.
    /// </param>
    public static ResolvedPackageInputs? Resolve(
        string manifestDir,
        PackageManifest manifest,
        DiagnosticBag diagnostics,
        bool resolveNuGetDependencies = true,
        bool preferPrecompiledDependencies = false,
        string? cacheDirectory = null
    )
    {
        var assemblySearchPaths = new List<string>();
        var moduleSearchPaths = new List<string>();
        var packagePaths = new Dictionary<string, string>();
        var moduleAliases = new Dictionary<string, string>();
        var nugetDeps = new List<NuGetDependency>(manifest.Dependencies.NuGet);
        var frameworkIds = new List<string>();
        var precompiledPackagePaths = new List<string>();

        // Walk the full transitive closure so a dep-of-a-dep's prefixed modules resolve
        // without the consumer re-declaring them (e.g. depending on aspnet → stdlib/...).
        // A dependency's framework / NuGet / ref-path inputs come along too, so its sources
        // can resolve their own CLR types when recompiled inside this build.
        if (manifest.Dependencies.ZScheme.Count > 0)
        {
            var closure = PackageDependencyResolver.ResolveTransitiveClosure(
                manifest.Dependencies.ZScheme,
                manifestDir,
                diagnostics
            );
            if (diagnostics.HasErrors)
                return null;

            var wiring = PackageDependencyWiring.For(
                closure,
                preferPrecompiledDependencies,
                diagnostics,
                cacheDirectory
            );

            moduleSearchPaths.AddRange(wiring.ModuleSearchPaths);
            foreach (var (prefix, path) in wiring.PackagePaths)
                packagePaths[prefix] = path;
            foreach (var (prefix, alias) in wiring.ModuleAliases)
                moduleAliases[prefix] = alias;
            precompiledPackagePaths.AddRange(wiring.PrecompiledAssemblyPaths);

            // Frameworks, NuGet and ref paths come along whether a dependency was referenced or
            // compiled. Metadata carries no assemblyHint for anything but a type alias, so a
            // dependency's `import-clr :instance` members are re-resolved by name in the consumer's
            // compilation and need the same reference assemblies either way.
            assemblySearchPaths.AddRange(
                FrameworkResolver.Resolve(closure.Frameworks, diagnostics)
            );
            assemblySearchPaths.AddRange(closure.RefPaths);
            nugetDeps.AddRange(closure.NuGet);
            frameworkIds.AddRange(closure.Frameworks.Select(f => f.Id));

            if (diagnostics.HasErrors)
                return null;

            Log.Debug(
                "PackageOptionsBuilder: ZScheme dependencies resolved (transitive), {PathCount} module search paths",
                moduleSearchPaths.Count
            );
        }

        // The consumer's own shared-framework references (e.g. Microsoft.AspNetCore.App),
        // so entry + dependency sources can resolve framework types.
        assemblySearchPaths.AddRange(
            FrameworkResolver.Resolve(manifest.Dependencies.Frameworks, diagnostics)
        );
        frameworkIds.AddRange(manifest.Dependencies.Frameworks.Select(f => f.Id));
        if (diagnostics.HasErrors)
            return null;

        if (resolveNuGetDependencies && nugetDeps.Count > 0)
        {
            var nugetOutputDir = new NuGetResolver(diagnostics).Resolve(nugetDeps);
            if (nugetOutputDir is null && diagnostics.HasErrors)
                return null;
            if (nugetOutputDir is not null)
            {
                assemblySearchPaths.Add(nugetOutputDir);
                Log.Debug(
                    "PackageOptionsBuilder: NuGet dependencies resolved to {OutputDir}",
                    nugetOutputDir
                );
            }
        }
        else if (nugetDeps.Count > 0)
        {
            Log.Debug(
                "PackageOptionsBuilder: skipping NuGet resolution for {Count} declared packages",
                nugetDeps.Count
            );
        }

        // The consumer's own manifest-level ref paths, relative to the manifest dir.
        if (manifest.Build.Main is { } mainBuild)
            foreach (var refPath in mainBuild.RefPaths)
                assemblySearchPaths.Add(Path.GetFullPath(Path.Combine(manifestDir, refPath)));

        return new ResolvedPackageInputs(
            assemblySearchPaths,
            moduleSearchPaths,
            packagePaths,
            moduleAliases,
            frameworkIds.Distinct().ToList()
        )
        {
            PrecompiledPackagePaths = precompiledPackagePaths,
        };
    }

    /// <summary>
    ///     Resolves the manifest and returns compiler options ready to compile it. Paths
    ///     from <paramref name="overrides" /> are layered last so an explicit caller
    ///     preference is probed before anything the manifest implies.
    /// </summary>
    /// <inheritdoc cref="Resolve" />
    public static CompilerOptions? BuildForPackage(
        string manifestDir,
        PackageManifest manifest,
        DiagnosticBag diagnostics,
        CompilerOptions? overrides = null,
        bool resolveNuGetDependencies = true,
        bool preferPrecompiledDependencies = false
    )
    {
        var inputs = Resolve(
            manifestDir,
            manifest,
            diagnostics,
            resolveNuGetDependencies,
            preferPrecompiledDependencies,
            overrides?.CacheDirectory
        );
        if (inputs is null)
            return null;

        var options = new CompilerOptions
        {
            OutputMode = manifest.Build.Main?.Backend ?? overrides?.OutputMode ?? OutputMode.CSharp,
            Namespace =
                manifest.Build.Main?.Namespace ?? overrides?.Namespace ?? "ZSchemeGenerated",
            PackagePaths = new Dictionary<string, string>(inputs.PackagePaths),
            ModuleAliases = new Dictionary<string, string>(inputs.ModuleAliases),
            FrameworkReferences = [.. inputs.FrameworkIds],
        };

        // Caller-supplied paths first: an in-process host's live output directory must win
        // over a manifest (ref ...) that names a possibly-stale build output directory.
        if (overrides is not null)
        {
            AddDistinct(options.AssemblySearchPaths, overrides.AssemblySearchPaths);
            AddDistinct(options.ModuleSearchPaths, overrides.ModuleSearchPaths);
            AddDistinct(options.PrecompiledPackagePaths, overrides.PrecompiledPackagePaths);
            foreach (var (prefix, path) in overrides.PackagePaths)
                options.PackagePaths[prefix] = path;
            foreach (var (prefix, alias) in overrides.ModuleAliases)
                options.ModuleAliases[prefix] = alias;
        }

        AddDistinct(options.AssemblySearchPaths, inputs.AssemblySearchPaths);
        AddDistinct(options.ModuleSearchPaths, inputs.ModuleSearchPaths);
        AddDistinct(options.PrecompiledPackagePaths, inputs.PrecompiledPackagePaths);
        return options;
    }

    /// <summary>
    ///     Appends <paramref name="additions" /> to <paramref name="target" />, skipping
    ///     entries already present (case-insensitive, treating values as file-system paths).
    /// </summary>
    internal static void AddDistinct(List<string> target, IEnumerable<string> additions)
    {
        var seen = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
        foreach (var item in additions)
            if (seen.Add(item))
                target.Add(item);
    }
}
