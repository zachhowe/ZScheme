using Serilog;
using ZScript.Compiler.Modules;

namespace ZScript.Compiler.Cache;

public sealed class PackageCacheManager(string? cacheRoot = null)
{
    private readonly string _cacheRoot = cacheRoot ?? ZScriptPaths.GetPackageCacheRoot();

    public PrecompiledPackage? TryLoad(string packageName, string version)
    {
        var packageDir = GetPackageDir(packageName, version);
        var assemblyPath = Path.Combine(packageDir, $"{packageName}.dll");
        var metadataPath = Path.Combine(packageDir, $"{packageName}.metadata.json");

        Log.Debug("PackageCache: looking up {PackageName}@{Version} at {Path}", packageName, version, assemblyPath);

        if (!File.Exists(assemblyPath) || !File.Exists(metadataPath))
        {
            Log.Debug("PackageCache: miss for {PackageName}@{Version}", packageName, version);
            return null;
        }

        var json = File.ReadAllText(metadataPath);
        Log.Debug("PackageCache: hit for {PackageName}@{Version}", packageName, version);
        return MetadataSerializer.Deserialize(json, assemblyPath);
    }

    public void Store(string packageName, string version, byte[] assemblyBytes,
        IReadOnlyDictionary<string, CompiledModule> modules)
    {
        var packageDir = GetPackageDir(packageName, version);
        Directory.CreateDirectory(packageDir);

        var assemblyPath = Path.Combine(packageDir, $"{packageName}.dll");
        File.WriteAllBytes(assemblyPath, assemblyBytes);

        var metadataJson = MetadataSerializer.Serialize(packageName, version, packageName, modules);
        var metadataPath = Path.Combine(packageDir, $"{packageName}.metadata.json");
        File.WriteAllText(metadataPath, metadataJson);

        Log.Debug("PackageCache: stored {PackageName}@{Version} ({ByteCount} bytes, {ModuleCount} modules) at {Path}",
            packageName, version, assemblyBytes.Length, modules.Count, packageDir);
    }

    public void Invalidate(string packageName, string version)
    {
        var packageDir = GetPackageDir(packageName, version);
        if (Directory.Exists(packageDir))
        {
            Log.Debug("PackageCache: invalidating {PackageName}@{Version} at {Path}", packageName, version, packageDir);
            Directory.Delete(packageDir, true);
        }
        else
        {
            Log.Debug("PackageCache: nothing to invalidate for {PackageName}@{Version}", packageName, version);
        }
    }

    private string GetPackageDir(string packageName, string version)
    {
        return Path.Combine(_cacheRoot, packageName, version);
    }
}
