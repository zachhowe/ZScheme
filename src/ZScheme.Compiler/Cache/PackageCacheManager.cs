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
        Directory.CreateDirectory(packageDir);

        var assemblyPath = Path.Combine(packageDir, $"{packageName}.dll");
        File.WriteAllBytes(assemblyPath, assemblyBytes);

        var metadataJson = MetadataSerializer.Serialize(
            packageName,
            version,
            packageName,
            modules,
            importPrefix,
            defaultModule
        );
        var metadataPath = Path.Combine(packageDir, $"{packageName}.metadata.json");
        File.WriteAllText(metadataPath, metadataJson);

        Log.Debug(
            "PackageCache: stored {PackageName}@{Version} ({ByteCount} bytes, {ModuleCount} modules) at {Path}",
            packageName,
            version,
            assemblyBytes.Length,
            modules.Count,
            packageDir
        );
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
