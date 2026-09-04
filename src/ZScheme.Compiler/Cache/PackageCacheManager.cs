using Serilog;
using ZScheme.Compiler.Modules;

namespace ZScheme.Compiler.Cache;

/// <summary>What a caller of <see cref="PackageCacheManager.Store" /> needs to be true of the
///     cache once the store returns.</summary>
public enum StoreRequirement
{
    /// <summary>
    ///     The entry at the version directory must be the build that was handed to Store.
    ///     <c>zs install</c> publishes a build a developer just asked it to publish, so an entry
    ///     some other writer put there instead is not what was asked for, however well formed.
    /// </summary>
    ThisBuild,

    /// <summary>
    ///     Any complete entry for the same package and version will do. The auto-installer
    ///     compiled the package only because its own lookup missed; a peer's entry for that
    ///     version is exactly what a hit would have handed back, and taking it is what every
    ///     other compile on the machine did.
    /// </summary>
    AnyBuildOfThisVersion,
}

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

    /// <summary>
    ///     Publishes a build into the cache and returns the entry a later lookup will hand back.
    /// </summary>
    /// <remarks>
    ///     The entry comes from the metadata that was just written, not from reading the cache
    ///     back: that read is a lookup like any other, and a caller which has just cached a
    ///     package successfully must not be told the package cannot be found because a peer's
    ///     commit landed in it. Only a store that accepted a peer's entry has to look, since what
    ///     is published there is that writer's build and not this one's.
    /// </remarks>
    public PrecompiledPackage? Store(
        string packageName,
        string version,
        byte[] assemblyBytes,
        IReadOnlyDictionary<string, CompiledModule> modules,
        string? importPrefix = null,
        string? defaultModule = null,
        StoreRequirement requirement = StoreRequirement.ThisBuild,
        IReadOnlyList<PrecompiledPackageDependency>? dependencies = null,
        string? inputFingerprint = null
    )
    {
        var packageDir = GetPackageDir(packageName, version);
        var assemblyPath = Path.Combine(packageDir, $"{packageName}.dll");
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
                defaultModule,
                dependencies,
                inputFingerprint
            );
            File.WriteAllText(
                Path.Combine(staging, $"{packageName}.metadata.json"),
                metadataJson
            );

            // A commit that did not publish this build must not pass for one that did. The
            // version directory is a name, not a content hash: whatever is left there when the
            // rename cannot happen is some other build, so reporting success would have
            // `zs install` print "cached at ..." over a package that is not the one it just
            // compiled, and every later compile would link that. Writing straight into packageDir
            // used to fail loudly here for the same reason -- "the process cannot access the file
            // ... because it is being used by another process" -- and it should still.
            var commit = AtomicDirectory.Commit(staging, packageDir);
            if (PublishFailure(commit, requirement, packageName, version, packageDir) is { } why)
                throw new IOException(why);

            Log.Debug(
                "PackageCache: stored {PackageName}@{Version} ({ByteCount} bytes, {ModuleCount} modules) at {Path}",
                packageName,
                version,
                assemblyBytes.Length,
                modules.Count,
                packageDir
            );

            return commit is CommitResult.Committed
                ? MetadataSerializer.Deserialize(metadataJson, assemblyPath)
                : TryLoad(packageName, version);
        }
        finally
        {
            AtomicDirectory.TryDelete(staging);
        }
    }

    /// <summary>
    ///     Why a commit's outcome cannot pass for a store of this build, or null when it can.
    /// </summary>
    /// <remarks>
    ///     Which outcomes are good enough is the caller's to say, and the two callers differ --
    ///     see <see cref="StoreRequirement" />. Kept apart from <see cref="Store" /> because the
    ///     races that produce anything but <see cref="CommitResult.Committed" /> cannot be staged
    ///     from a test, so this is where the decision itself is checked.
    /// </remarks>
    internal static string? PublishFailure(
        CommitResult commit,
        StoreRequirement requirement,
        string packageName,
        string version,
        string packageDir
    ) =>
        commit switch
        {
            CommitResult.Committed => null,

            // A peer published its own build under this name and version, and nothing says the
            // two writers built the same thing. A caller that needs a build of this version has
            // what it came for; one publishing the build it was handed has not, and used to be
            // told it had -- the one outcome Store failed to check, which put `zs install` back
            // to reporting success over a package it did not cache.
            CommitResult.PeerWon when requirement is StoreRequirement.AnyBuildOfThisVersion => null,
            CommitResult.PeerWon =>
                $"Another process published {packageName} v{version} at {packageDir} first; "
                + "that build is what is cached, not this one.",

            // The entry that could not be displaced is a complete one for this package and
            // version. A caller that only needs a build of that version is looking at what its
            // own lookup would have hit had the peer published a moment earlier -- it compiled
            // because that lookup missed, and the entry appeared while it was compiling. Failing
            // the compile over a usable entry buys nothing, and on Windows the handle that
            // blocks the rename is as often a scanner reading the .dll a peer just wrote as it
            // is a process with the entry loaded.
            CommitResult.Blocked
                when requirement is StoreRequirement.AnyBuildOfThisVersion
                    && HasEntry(packageName, packageDir) => null,
            CommitResult.Blocked =>
                $"Could not replace the cached {packageName} v{version} at {packageDir}: "
                + $"another process is most likely holding {packageName}.dll open. "
                + "The previous build is still what is cached.",

            _ => $"Could not cache {packageName} v{version} at {packageDir}.",
        };

    /// <summary>Whether the version directory holds an entry a lookup would hit: both files.</summary>
    private static bool HasEntry(string packageName, string packageDir) =>
        File.Exists(Path.Combine(packageDir, $"{packageName}.dll"))
        && File.Exists(Path.Combine(packageDir, $"{packageName}.metadata.json"));

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
