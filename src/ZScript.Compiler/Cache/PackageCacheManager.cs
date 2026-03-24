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

        if (!File.Exists(assemblyPath) || !File.Exists(metadataPath))
            return null;

        var json = File.ReadAllText(metadataPath);
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
    }

    public void Invalidate(string packageName, string version)
    {
        var packageDir = GetPackageDir(packageName, version);
        if (Directory.Exists(packageDir))
            Directory.Delete(packageDir, true);
    }

    private string GetPackageDir(string packageName, string version)
    {
        return Path.Combine(_cacheRoot, packageName, version);
    }
}
