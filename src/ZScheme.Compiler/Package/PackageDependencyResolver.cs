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
            refPaths
        );
    }
}
