using Serilog;
using ZScheme.Compiler.Modules;

namespace ZScheme.Compiler.Cache;

public sealed class PackageCacheManager(string? cacheRoot = null)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PackageCacheManager>();

    private readonly string _cacheRoot = cacheRoot ?? ZSchemePaths.GetPackageCacheRoot();

    public PrecompiledPackage? TryLoad(string packageName, string version)
    {
        var packageDir = GetPackageDir(packageName, version);
        var assemblyPath = Path.Combine(packageDir, $"{packageName}.dll");
        var metadataPath = Path.Combine(packageDir, $"{packageName}.metadata.json");

        Log.Debug(
            "PackageCache: looking up {PackageName}@{Version} at {Path}",
            packageName,
            version,
            assemblyPath
        );

        // No directory for the package name at all: there is no entry and no commit under way
        // either — publishing one renames the version directory, never its parent — so the
        // retries below would only sleep on a lookup that has already settled.
        if (!Directory.Exists(Path.GetDirectoryName(packageDir)!))
        {
            Log.Debug("PackageCache: miss for {PackageName}@{Version}", packageName, version);
            return null;
        }

        for (var attempt = 1; ; attempt++)
        {
            // Read the metadata rather than test for it and then read it. An entry is published
            // by renaming it in over whatever was there (see AtomicDirectory), so the version
            // directory is briefly absent while a peer swaps its copy in: File.Exists returning
            // true said nothing about the read that followed, and that read threw
            // FileNotFoundException on an entry that was never half-written — out through
            // Compilation.TryLoadPrecompiledModules, where nothing catches it, killing a compile
            // that had a perfectly good cache entry a moment later.
            string? json;
            try
            {
                json = File.ReadAllText(metadataPath);
                if (!File.Exists(assemblyPath))
                    json = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                json = null;
            }

            if (json is not null)
            {
                Log.Debug("PackageCache: hit for {PackageName}@{Version}", packageName, version);
                return MetadataSerializer.Deserialize(json, assemblyPath);
            }

            if (attempt == MaxLoadAttempts)
            {
                Log.Debug("PackageCache: miss for {PackageName}@{Version}", packageName, version);
                return null;
            }

            // Let the commit that is mid-swap land rather than calling its window a miss, which
            // sends the caller off to recompile and re-store a package that is already cached.
            Thread.Sleep(LoadBackoffMs * attempt);
        }
    }

    /// <summary>How many times a lookup that found nothing is retried before it counts as a miss.</summary>
    /// <remarks>
    ///     Every window in which the version directory is absent belongs to a peer that is
    ///     mid-commit, and it is over in the time two renames take. Matches the budget
    ///     <see cref="AtomicDirectory" /> gives a commit for the mirror-image reason: neither side
    ///     of a swap settles anything from a single attempt.
    /// </remarks>
    private const int MaxLoadAttempts = 5;

    /// <summary>Base of the linear backoff between lookup attempts, in milliseconds.</summary>
    private const int LoadBackoffMs = 10;

    public void Store(
        string packageName,
        string version,
        byte[] assemblyBytes,
        IReadOnlyDictionary<string, CompiledModule> modules,
        string? importPrefix = null,
        string? defaultModule = null
    )
    {
        var packageDir = GetPackageDir(packageName, version);
        var parent = Path.GetDirectoryName(packageDir)!;
        Directory.CreateDirectory(parent);

        // Assemble under a private name and rename it in (see AtomicDirectory). Writing the
        // assembly and its metadata straight into packageDir left a window where the .dll was
        // there and the metadata was not, which TryLoad reads as a miss: a reader in that window
        // auto-installed the package again and its File.WriteAllBytes collided with the writer
        // still holding that .dll open.
        var staging = AtomicDirectory.StagingPathFor(packageDir);
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllBytes(Path.Combine(staging, $"{packageName}.dll"), assemblyBytes);

            var metadataJson = MetadataSerializer.Serialize(
                packageName,
                version,
                packageName,
                modules,
                importPrefix,
                defaultModule
            );
            File.WriteAllText(
                Path.Combine(staging, $"{packageName}.metadata.json"),
                metadataJson
            );

            // A commit that did not publish this build must not pass for one that did. The
            // version directory is a name, not a content hash: whatever is left there when the
            // rename cannot happen is the *previous* build, so reporting success would have
            // `zs install` print "cached at ..." over a package that is still the old one, and
            // every later compile would link that. Writing straight into packageDir used to fail
            // loudly here for the same reason -- "the process cannot access the file ... because
            // it is being used by another process" -- and it should still.
            var commit = AtomicDirectory.Commit(staging, packageDir);
            if (commit is CommitResult.Blocked)
                throw new IOException(
                    $"Could not replace the cached {packageName} v{version} at {packageDir}: "
                        + $"another process is most likely holding {packageName}.dll open. "
                        + "The previous build is still what is cached."
                );
            if (commit is CommitResult.Failed)
                throw new IOException(
                    $"Could not cache {packageName} v{version} at {packageDir}."
                );

            Log.Debug(
                "PackageCache: stored {PackageName}@{Version} ({ByteCount} bytes, {ModuleCount} modules) at {Path}",
                packageName,
                version,
                assemblyBytes.Length,
                modules.Count,
                packageDir
            );
        }
        finally
        {
            AtomicDirectory.TryDelete(staging);
        }
    }

    public PrecompiledPackage? TryLoadLatest(string packageName)
    {
        var packageRoot = Path.Combine(_cacheRoot, packageName);
        if (!Directory.Exists(packageRoot))
        {
            Log.Debug("PackageCache: no versions found for {PackageName}", packageName);
            return null;
        }

        Version? bestVersion = null;
        string? bestDirName = null;

        foreach (var dir in Directory.GetDirectories(packageRoot))
        {
            var dirName = Path.GetFileName(dir);
            if (Version.TryParse(dirName, out var v) && (bestVersion is null || v > bestVersion))
            {
                bestVersion = v;
                bestDirName = dirName;
            }
        }

        if (bestDirName is null)
        {
            Log.Debug("PackageCache: no valid versions found for {PackageName}", packageName);
            return null;
        }

        Log.Debug(
            "PackageCache: resolved latest {PackageName} to {Version}",
            packageName,
            bestDirName
        );
        return TryLoad(packageName, bestDirName);
    }

    public void Invalidate(string packageName, string version)
    {
        var packageDir = GetPackageDir(packageName, version);
        if (Directory.Exists(packageDir))
        {
            Log.Debug(
                "PackageCache: invalidating {PackageName}@{Version} at {Path}",
                packageName,
                version,
                packageDir
            );
            Directory.Delete(packageDir, true);
        }
        else
        {
            Log.Debug(
                "PackageCache: nothing to invalidate for {PackageName}@{Version}",
                packageName,
                version
            );
        }
    }

    private string GetPackageDir(string packageName, string version)
    {
        return Path.Combine(_cacheRoot, packageName, version);
    }
}
