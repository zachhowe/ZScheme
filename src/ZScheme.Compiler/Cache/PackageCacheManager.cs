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

        if (!File.Exists(assemblyPath) || !File.Exists(metadataPath))
        {
            Log.Debug("PackageCache: miss for {PackageName}@{Version}", packageName, version);
            return null;
        }

        var json = File.ReadAllText(metadataPath);
        Log.Debug("PackageCache: hit for {PackageName}@{Version}", packageName, version);
        return MetadataSerializer.Deserialize(json, assemblyPath);
    }

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

            AtomicDirectory.Commit(staging, packageDir);

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
