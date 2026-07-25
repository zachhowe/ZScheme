using System.Collections.Concurrent;
using Serilog;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Package;

/// <summary>
///     Automatically discovers, compiles, and caches packages from source
///     when they are not found in the package cache.
/// </summary>
public static class PackageAutoInstaller
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(PackageAutoInstaller));

    private static readonly ConcurrentDictionary<string, object> InstallLocks = new();

    /// <summary>
    ///     Attempts to find a package's source in a nearby <c>packages/</c> directory,
    ///     compile it, cache the result, and return the loaded package.
    ///     Returns <c>null</c> if the source cannot be found or compilation fails.
    /// </summary>
    public static PrecompiledPackage? TryAutoInstall(
        string packageName,
        string? anchorDir,
        DiagnosticBag diagnostics,
        string? cacheDirectory = null
    )
    {
        var lockObj = InstallLocks.GetOrAdd(packageName, _ => new object());
        lock (lockObj)
        {
            // Double-check cache after acquiring lock (another thread may have installed it)
            var cacheManager = new PackageCacheManager(
                ZSchemePaths.GetPackageCacheRoot(cacheDirectory)
            );
            var cached = cacheManager.TryLoadLatest(packageName);
            if (cached is not null)
                return cached;

            var source = FindPackageSource(packageName, anchorDir);
            if (source is null)
            {
                Log.Debug("PackageAutoInstaller: no source found for {PackageName}", packageName);
                return null;
            }

            Log.Information(
                "PackageAutoInstaller: auto-installing {PackageName} from {PackageDir}",
                packageName,
                source.Value.PackageDir
            );

            var manifest = source.Value.Manifest;
            var packageDir = source.Value.PackageDir;

            // Resolve NuGet dependencies
            var assemblySearchPaths = new List<string>();
            if (manifest.Dependencies.NuGet.Count > 0)
            {
                var installDiag = new DiagnosticBag();
                var nugetResolver = new NuGetResolver(installDiag);
                var nugetOutputDir = nugetResolver.Resolve(manifest.Dependencies.NuGet);
                if (nugetOutputDir is null && installDiag.HasErrors)
                {
                    diagnostics.AddRange(installDiag);
                    return null;
                }

                if (nugetOutputDir is not null)
                    assemblySearchPaths.Add(nugetOutputDir);
            }

            // Resolve shared frameworks (e.g. Microsoft.AspNetCore.App) so this package's own
            // sources can resolve framework types. Every other compile path does this
            // (PackageBuilder, PackageTester, CliHelpers, the language server); omitting it here
            // meant a package with a (framework ...) dep was auto-installed without its reference
            // assemblies, which the LSP hit on every aspnet import.
            //
            // Both the manifest's own frameworks and those it inherits through the transitive
            // ZScheme dependency closure count, matching PackageBuilder: a package that depends on
            // one declaring (framework ...) without redeclaring it is compiled from source here
            // too, so it needs the same reference paths. The closure walk uses its own diagnostic
            // bag — an unresolvable dependency is a soft "cannot auto-install" signal, and the
            // LibraryCompiler run below reports what actually failed to compile.
            var closureDiag = new DiagnosticBag();
            var closure = PackageDependencyResolver.ResolveTransitiveClosure(
                manifest.Dependencies.ZScheme,
                packageDir,
                closureDiag
            );

            var frameworks = new List<FrameworkDependency>(closure.Frameworks);
            frameworks.AddRange(manifest.Dependencies.Frameworks);
            if (frameworks.Count > 0)
            {
                var frameworkDiag = new DiagnosticBag();
                var frameworkPaths = FrameworkResolver.Resolve(frameworks, frameworkDiag);
                if (frameworkDiag.HasErrors)
                {
                    diagnostics.AddRange(frameworkDiag);
                    return null;
                }

                // A framework declared by the package *and* inherited resolves to the same
                // directory twice; duplicate search paths would mint a second InteropLoadContext
                // for an equivalent path set.
                foreach (var path in frameworkPaths)
                    if (!assemblySearchPaths.Contains(path, StringComparer.Ordinal))
                        assemblySearchPaths.Add(path);
            }

            // Resolve ZScheme dependencies from manifest. Deliberately the *direct* local deps
            // only, not `closure` above: this fixes the framework gap without also widening which
            // modules an auto-installed package can import, which is a separate change.
            var packagePaths = new Dictionary<string, string>();
            var moduleAliases = new Dictionary<string, string>();
            foreach (var dep in manifest.Dependencies.ZScheme)
                if (dep.Source is ZSchemeDependencySource.Local local)
                {
                    var depDir = Path.GetFullPath(Path.Combine(packageDir, local.Path));
                    var depInfo = ResolvePackagePath(depDir);
                    if (depInfo is not null)
                    {
                        packagePaths.TryAdd(depInfo.Value.Prefix, depInfo.Value.SourceDir);
                        if (depInfo.Value.DefaultModule is { } defMod)
                            moduleAliases.TryAdd(
                                depInfo.Value.Prefix,
                                $"{depInfo.Value.Prefix}/{defMod}"
                            );
                    }
                }

            // Add manifest-level ref paths (main build config)
            if (manifest.Build.Main is { } mainBuild)
                foreach (var refPath in mainBuild.RefPaths)
                    assemblySearchPaths.Add(Path.GetFullPath(Path.Combine(packageDir, refPath)));

            var options = new CompilerOptions
            {
                AssemblySearchPaths = assemblySearchPaths,
                PackagePaths = packagePaths,
                ModuleAliases = moduleAliases,
                CacheDirectory = cacheDirectory,
            };

            // Compile the package
            var installDiagnostics = new DiagnosticBag();
            var libraryCompiler = new LibraryCompiler(installDiagnostics);
            var result = libraryCompiler.Compile(packageDir, manifest, options);
            if (result is null)
            {
                diagnostics.AddRange(installDiagnostics);
                return null;
            }

            // Store in cache
            cacheManager.Store(
                manifest.Name,
                manifest.Version,
                result.AssemblyBytes,
                result.Modules,
                manifest.ImportPrefix,
                manifest.DefaultModule
            );

            Log.Information(
                "PackageAutoInstaller: cached {PackageName}@{Version}",
                manifest.Name,
                manifest.Version
            );

            return cacheManager.TryLoad(manifest.Name, manifest.Version);
        }
    }

    /// <summary>
    ///     Walks up the directory tree from <paramref name="anchorDir" /> looking for
    ///     a <c>packages/*/package.zspkg</c> whose manifest name matches <paramref name="packageName" />.
    /// </summary>
    private static (string PackageDir, PackageManifest Manifest)? FindPackageSource(
        string packageName,
        string? anchorDir
    )
    {
        // Try anchor dir first, then fall back to CWD if different
        var anchor = anchorDir is not null ? Path.GetFullPath(anchorDir) : null;
        var cwd = Directory.GetCurrentDirectory();

        var result = anchor is not null ? ScanUpForPackage(packageName, anchor) : null;
        if (result is null && (anchor is null || !cwd.Equals(anchor, StringComparison.Ordinal)))
            result = ScanUpForPackage(packageName, cwd);

        return result;
    }

    private static (string PackageDir, PackageManifest Manifest)? ScanUpForPackage(
        string packageName,
        string startDir
    )
    {
        var dir = startDir;

        for (var depth = 0; depth < 10; depth++)
        {
            var packagesDir = Path.Combine(dir, "packages");
            if (Directory.Exists(packagesDir))
                foreach (var subDir in Directory.GetDirectories(packagesDir))
                {
                    var manifestPath = Path.Combine(subDir, "package.zspkg");
                    if (!File.Exists(manifestPath))
                        continue;

                    var diag = new DiagnosticBag();
                    var parser = new ManifestParser(diag);
                    var manifest = parser.Parse(File.ReadAllText(manifestPath), manifestPath);
                    if (manifest is null || diag.HasErrors)
                        continue;

                    if (manifest.Name == packageName)
                    {
                        Log.Debug(
                            "PackageAutoInstaller: found source for {PackageName} at {Dir}",
                            packageName,
                            subDir
                        );
                        return (subDir, manifest);
                    }
                }

            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;
            dir = parent.FullName;
        }

        return null;
    }

    /// <summary>
    ///     Reads a package manifest to extract import prefix and source directory.
    ///     Equivalent to <c>CliHelpers.ResolvePackagePath</c> but uses diagnostics instead of console output.
    /// </summary>
    private static (string Prefix, string SourceDir, string? DefaultModule)? ResolvePackagePath(
        string packageDir
    )
    {
        var fullDir = Path.GetFullPath(packageDir);
        var manifestPath = Path.Combine(fullDir, "package.zspkg");
        if (!File.Exists(manifestPath))
            return null;

        var diag = new DiagnosticBag();
        var parser = new ManifestParser(diag);
        var manifest = parser.Parse(File.ReadAllText(manifestPath), manifestPath);
        if (manifest is null || diag.HasErrors || manifest.ImportPrefix is null)
            return null;

        var sourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(fullDir, manifest.Sources.Main))
            : fullDir;

        return (manifest.ImportPrefix, sourceDir, manifest.DefaultModule);
    }
}
