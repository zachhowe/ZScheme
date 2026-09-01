using Serilog;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Package;

/// <summary>
///     A dependency's built assembly, ready to be referenced.
/// </summary>
public sealed record PackageArtifact(
    string Name,
    string Version,
    string AssemblyPath,
    string MetadataPath
);

/// <summary>
///     Answers "is there a built assembly for this dependency that matches its sources?", building
///     one when there is not. This is what lets a package reference a dependency instead of
///     compiling its sources into itself.
///     <para>
///         Nothing calls this yet — the entry points that resolve a manifest's dependencies still
///         put every dependency's source directory on the module search path.
///     </para>
/// </summary>
public static class PackageArtifactResolver
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(PackageArtifactResolver));

    /// <summary>
    ///     Resolves <paramref name="package" /> to an artifact, rebuilding it when the cached one
    ///     is missing or stale. Returns <c>null</c> when no artifact can be produced — a package
    ///     whose manifest carries no name or version is not addressable in the cache, and a build
    ///     that fails leaves the caller to fall back to compiling from source.
    /// </summary>
    public static PackageArtifact? Resolve(
        ResolvedPackage package,
        DiagnosticBag diagnostics,
        string? cacheDirectory = null
    )
    {
        if (package.Name.Length == 0 || package.Version.Length == 0)
        {
            Log.Debug(
                "PackageArtifactResolver: {PackageDir} has no manifest identity to key a cache entry by",
                package.PackageDir
            );
            return null;
        }

        var cache = new PackageCacheManager(ZSchemePaths.GetPackageCacheRoot(cacheDirectory));
        var cached = cache.TryLoad(package.Name, package.Version);

        if (cached is not null && IsFresh(cached, package))
            return ToArtifact(cache, package);

        Log.Debug(
            "PackageArtifactResolver: rebuilding {PackageName}@{Version} ({Reason})",
            package.Name,
            package.Version,
            cached is null ? "no cached artifact" : "stale"
        );

        var manifest = ReadManifest(package.PackageDir);
        if (manifest is null)
            return null;

        var rebuilt = PackageAutoInstaller.TryAutoInstall(
            package.Name,
            package.PackageDir,
            diagnostics,
            cacheDirectory,
            (package.PackageDir, manifest),
            ignoreCache: true
        );

        return rebuilt is null ? null : ToArtifact(cache, package);
    }

    /// <summary>
    ///     An artifact is current when the package's own sources hash to what was recorded, and
    ///     every dependency it was built against is still offered at the same version and hash.
    ///     <para>
    ///         Both halves are needed. Own-hash alone misses a dependency that changed after this
    ///         package was built — its signatures moved, but nothing in this package's own sources
    ///         did. Dependency hashes alone miss an edit to the package itself.
    ///     </para>
    ///     <para>
    ///         An artifact recorded without a fingerprint — written by a compiler from before the
    ///         field existed — is treated as stale rather than trusted, so the first build after an
    ///         upgrade re-establishes the invariant instead of inheriting an unverifiable one.
    ///     </para>
    /// </summary>
    public static bool IsFresh(PrecompiledPackage artifact, ResolvedPackage package)
    {
        if (artifact.InputFingerprint is not { } recorded)
            return false;

        var current = PackageFingerprint.Compute(package.PackageDir, package.SourceDir);
        if (current is null || !string.Equals(current, recorded, StringComparison.Ordinal))
            return false;

        var currentDeps = PackageDependencyResolver.ResolveDependencyIdentities(
            package.ZSchemeDeps,
            package.PackageDir
        );

        if (currentDeps.Count != artifact.Dependencies.Count)
            return false;

        var recordedByName = artifact.Dependencies.ToDictionary(
            d => d.Name,
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var dep in currentDeps)
        {
            if (!recordedByName.TryGetValue(dep.Name, out var was))
                return false;
            if (!string.Equals(dep.Version, was.Version, StringComparison.Ordinal))
                return false;
            // A dependency that cannot be fingerprinted now, or could not be then, cannot vouch
            // for the artifact either way.
            if (dep.Fingerprint is null || was.Fingerprint is null)
                return false;
            if (!string.Equals(dep.Fingerprint, was.Fingerprint, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static PackageArtifact? ToArtifact(PackageCacheManager cache, ResolvedPackage package)
    {
        var loaded = cache.TryLoad(package.Name, package.Version);
        if (loaded is null)
            return null;

        return new PackageArtifact(
            package.Name,
            package.Version,
            loaded.AssemblyPath,
            Path.ChangeExtension(loaded.AssemblyPath, ".metadata.json")
        );
    }

    private static PackageManifest? ReadManifest(string packageDir)
    {
        var manifestPath = Path.Combine(packageDir, "package.zspkg");
        if (!File.Exists(manifestPath))
            return null;

        // A malformed manifest is a soft "cannot produce an artifact" here; the caller falls back
        // to source, and whatever compiles that source reports the real parse error.
        var diagnostics = new DiagnosticBag();
        var manifest = new ManifestParser(diagnostics).Parse(
            File.ReadAllText(manifestPath),
            manifestPath
        );
        return diagnostics.HasErrors ? null : manifest;
    }
}
