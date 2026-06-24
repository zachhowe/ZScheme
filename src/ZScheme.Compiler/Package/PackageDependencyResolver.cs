using Serilog;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Package;

/// <summary>
///     Reads a local ZScheme package's <c>package.zspkg</c> to surface everything a
///     consumer needs to compile against it from source: its import prefix and source
///     directory (for prefixed module resolution) plus its framework/NuGet/ref-path
///     dependencies (so the dependency's own sources can resolve their CLR types when
///     recompiled).
/// </summary>
public sealed record ResolvedPackage(
    string Prefix,
    string SourceDir,
    string? DefaultModule,
    IReadOnlyList<FrameworkDependency> Frameworks,
    IReadOnlyList<NuGetDependency> NuGet,
    IReadOnlyList<string> RefPaths,
    string PackageDir,
    IReadOnlyList<ZSchemeDependency> ZSchemeDeps
);

/// <summary>
///     The transitive closure of a consumer's ZScheme dependencies: everything needed to
///     compile the consumer and every (direct or indirect) dependency from source. Produced
///     by <see cref="PackageDependencyResolver.ResolveTransitiveClosure" />.
/// </summary>
public sealed record TransitiveZSchemeClosure(
    IReadOnlyList<string> ModuleSearchPaths,
    IReadOnlyDictionary<string, string> PackagePaths,
    IReadOnlyDictionary<string, string> ModuleAliases,
    IReadOnlyList<FrameworkDependency> Frameworks,
    IReadOnlyList<NuGetDependency> NuGet,
    IReadOnlyList<string> RefPaths
);

public static class PackageDependencyResolver
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(PackageDependencyResolver));

    /// <summary>
    ///     Attempts to read the package manifest at <paramref name="packageDir" /> and resolve
    ///     its prefix, source dir, and transitive build inputs. Returns <c>null</c> as a soft
    ///     signal — never writing to any caller diagnostics — when the directory has no
    ///     <c>package.zspkg</c>, the manifest defines no <c>import-prefix</c>, or the manifest
    ///     fails to parse. Callers that want a hard error (e.g. an explicit
    ///     <c>--package-path</c>) emit it themselves; callers that support bare directories
    ///     fall back to treating <paramref name="packageDir" /> as a plain module search path.
    /// </summary>
    public static ResolvedPackage? TryResolvePackage(string packageDir)
    {
        var fullDir = Path.GetFullPath(packageDir);
        var manifestPath = Path.Combine(fullDir, "package.zspkg");
        if (!File.Exists(manifestPath))
        {
            Log.Debug("TryResolvePackage: no package.zspkg in {PackageDir}", fullDir);
            return null;
        }

        // Swallow parse diagnostics into a throwaway bag: a malformed dependency manifest is
        // a soft "not a usable package" signal here, not a build-stopping error.
        var diag = new DiagnosticBag();
        var parser = new ManifestParser(diag);
        var manifest = parser.Parse(File.ReadAllText(manifestPath), manifestPath);
        if (manifest is null || diag.HasErrors)
        {
            Log.Debug(
                "TryResolvePackage: failed to parse manifest at {ManifestPath}",
                manifestPath
            );
            return null;
        }

        if (manifest.ImportPrefix is null)
        {
            Log.Debug("TryResolvePackage: package at {PackageDir} has no import-prefix", fullDir);
            return null;
        }

        var sourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(fullDir, manifest.Sources.Main))
            : fullDir;

        var refPaths = manifest.Build.Main is { } mainBuild
            ? mainBuild.RefPaths.Select(r => Path.GetFullPath(Path.Combine(fullDir, r))).ToList()
            : [];

        Log.Debug(
            "TryResolvePackage: {PackageDir} → prefix={Prefix}, sourceDir={SourceDir}",
            fullDir,
            manifest.ImportPrefix,
            sourceDir
        );

        return new ResolvedPackage(
            manifest.ImportPrefix,
            sourceDir,
            manifest.DefaultModule,
            manifest.Dependencies.Frameworks,
            manifest.Dependencies.NuGet,
            refPaths,
            fullDir,
            manifest.Dependencies.ZScheme
        );
    }

    /// <summary>
    ///     Walks the full transitive closure of <paramref name="rootDeps" /> — a consumer's
    ///     direct ZScheme dependencies — registering every reachable package's import prefix,
    ///     source dir, default-module alias, and build inputs (frameworks / NuGet / ref-paths).
    ///     A dependency's own ZScheme dependencies are followed recursively so that a
    ///     dep-of-a-dep's prefixed modules (e.g. depending on <c>aspnet</c> resolves
    ///     <c>stdlib/...</c>) are importable without re-declaring them on the consumer.
    ///     Relative <c>:local</c> paths in a dependency's manifest are resolved relative to
    ///     that dependency's directory, not the root consumer's.
    /// </summary>
    public static TransitiveZSchemeClosure ResolveTransitiveClosure(
        IReadOnlyList<ZSchemeDependency> rootDeps,
        string rootManifestDir,
        DiagnosticBag diagnostics,
        string? cacheRoot = null
    )
    {
        var moduleSearchPaths = new List<string>();
        var packagePaths = new Dictionary<string, string>();
        var moduleAliases = new Dictionary<string, string>();
        var frameworks = new List<FrameworkDependency>();
        var nuget = new List<NuGetDependency>();
        var refPaths = new List<string>();

        // BFS so direct deps are processed before transitive ones: first writer wins for a
        // shared prefix (TryAdd), letting a consumer shadow a transitive package's prefix.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(ZSchemeDependency Dep, string OwnerDir)>();
        foreach (var dep in rootDeps)
            queue.Enqueue((dep, rootManifestDir));

        while (queue.Count > 0)
        {
            var (dep, ownerDir) = queue.Dequeue();

            // A fresh resolver per item so relative :local paths root at the *owner's* dir.
            var resolver = new ZSchemeDependencyResolver(diagnostics, ownerDir, cacheRoot);
            var depDirs = resolver.Resolve([dep]);
            if (depDirs.Count == 0)
                continue; // resolution failed; ZSchemeDependencyResolver already recorded it

            var depDir = Path.GetFullPath(depDirs[0]);
            if (!visited.Add(depDir))
                continue; // already processed (diamond) or a cycle

            var resolved = TryResolvePackage(depDir);
            if (resolved is null)
            {
                // Bare dependency directory (no manifest / no import-prefix): expose it as a
                // plain module search path, preserving legacy unprefixed deps.
                moduleSearchPaths.Add(depDir);
                continue;
            }

            moduleSearchPaths.Add(resolved.SourceDir);
            packagePaths.TryAdd(resolved.Prefix, resolved.SourceDir);
            if (resolved.DefaultModule is { } defMod)
                moduleAliases.TryAdd(resolved.Prefix, $"{resolved.Prefix}/{defMod}");
            frameworks.AddRange(resolved.Frameworks);
            nuget.AddRange(resolved.NuGet);
            refPaths.AddRange(resolved.RefPaths);

            // Follow only main ZScheme deps — a dependency's test deps are not consumer-visible.
            foreach (var transitiveDep in resolved.ZSchemeDeps)
                queue.Enqueue((transitiveDep, resolved.PackageDir));
        }

        return new TransitiveZSchemeClosure(
            moduleSearchPaths,
            packagePaths,
            moduleAliases,
            frameworks,
            nuget,
            refPaths
        );
    }
}
