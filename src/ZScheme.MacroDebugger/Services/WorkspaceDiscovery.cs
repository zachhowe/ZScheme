using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.MacroDebugger.Services;

/// <summary>
///     Discovers the package environment around a <c>.zs</c> file so imports (and therefore
///     imported macros) resolve. Slim port of the language server's
///     <c>AnalysisService.DiscoverPackages</c>: it walks up from the file for a
///     <c>packages/</c> directory of <c>package.zspkg</c> manifests and, for files under a
///     package's test directory, merges that package's test dependencies. NuGet/framework
///     assembly resolution is intentionally omitted — the macro debugger stops at stage 2.5,
///     before anything needs CLR assemblies. When nothing is found, empty maps are returned
///     and the compiler falls back to the installed <c>zscheme-stdlib</c> package cache.
/// </summary>
public sealed record DiscoveredWorkspace(
    Dictionary<string, string> PackagePaths,
    Dictionary<string, string> ModuleAliases,
    List<string> ModuleSearchPaths
);

public static class WorkspaceDiscovery
{
    public static DiscoveredWorkspace Discover(string filePath)
    {
        var paths = new Dictionary<string, string>();
        var aliases = new Dictionary<string, string>();
        var extraSearchPaths = new List<string>();

        var fullFilePath = Path.GetFullPath(filePath);
        var (ownerManifest, ownerDir, isTestFile) = FindOwningPackage(fullFilePath);

        var dir = Path.GetDirectoryName(fullFilePath);
        while (dir is not null)
        {
            var packagesDir = Path.Combine(dir, "packages");
            if (Directory.Exists(packagesDir))
            {
                foreach (var sub in Directory.EnumerateDirectories(packagesDir))
                {
                    var manifestPath = Path.Combine(sub, "package.zspkg");
                    if (!File.Exists(manifestPath))
                        continue;

                    var diag = new DiagnosticBag();
                    var manifest = new ManifestParser(diag).Parse(
                        File.ReadAllText(manifestPath),
                        manifestPath
                    );
                    if (manifest?.ImportPrefix is null || diag.HasErrors)
                        continue;

                    RegisterManifest(manifest, sub, paths, aliases);
                }

                if (paths.Count > 0)
                    break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        if (isTestFile && ownerManifest is not null && ownerDir is not null)
            ApplyTestContext(ownerManifest, ownerDir, paths, aliases, extraSearchPaths);

        return new DiscoveredWorkspace(paths, aliases, extraSearchPaths);
    }

    private static (
        PackageManifest? Manifest,
        string? PackageDir,
        bool IsTestFile
    ) FindOwningPackage(string fullFilePath)
    {
        var dir = Path.GetDirectoryName(fullFilePath);
        while (dir is not null)
        {
            var manifestPath = Path.Combine(dir, "package.zspkg");
            if (File.Exists(manifestPath))
            {
                var diag = new DiagnosticBag();
                var manifest = new ManifestParser(diag).Parse(
                    File.ReadAllText(manifestPath),
                    manifestPath
                );
                if (manifest is null || diag.HasErrors)
                    return (null, null, false);

                var isTest = false;
                if (manifest.Sources?.Test is { } testRel)
                {
                    var testDir = Path.GetFullPath(Path.Combine(dir, testRel));
                    isTest = IsPathUnder(fullFilePath, testDir);
                }

                return (manifest, dir, isTest);
            }

            dir = Path.GetDirectoryName(dir);
        }

        return (null, null, false);
    }

    private static bool IsPathUnder(string filePath, string ancestorDir)
    {
        var normalized =
            Path.GetFullPath(ancestorDir).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return filePath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static void RegisterManifest(
        PackageManifest manifest,
        string packageDir,
        Dictionary<string, string> paths,
        Dictionary<string, string> aliases
    )
    {
        if (manifest.ImportPrefix is null)
            return;

        var sourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(packageDir, manifest.Sources.Main))
            : packageDir;

        paths[manifest.ImportPrefix] = sourceDir;

        if (manifest.DefaultModule is { } defMod)
            aliases.TryAdd(manifest.ImportPrefix, $"{manifest.ImportPrefix}/{defMod}");
    }

    private static void ApplyTestContext(
        PackageManifest ownerManifest,
        string ownerDir,
        Dictionary<string, string> paths,
        Dictionary<string, string> aliases,
        List<string> extraSearchPaths
    )
    {
        if (ownerManifest.Sources?.Test is { } testRel)
        {
            var testDir = Path.GetFullPath(Path.Combine(ownerDir, testRel));
            if (Directory.Exists(testDir))
                extraSearchPaths.Add(testDir);
        }

        if (ownerManifest.TestDependencies.ZScheme.Count == 0)
            return;

        var sink = new DiagnosticBag();
        List<string> depPaths;
        try
        {
            var resolver = new ZSchemeDependencyResolver(sink, ownerDir);
            depPaths = resolver.Resolve(ownerManifest.TestDependencies.ZScheme);
        }
        catch
        {
            return;
        }

        foreach (var depPath in depPaths)
        {
            var manifestPath = Path.Combine(depPath, "package.zspkg");
            if (!File.Exists(manifestPath))
                continue;

            var diag = new DiagnosticBag();
            var depManifest = new ManifestParser(diag).Parse(
                File.ReadAllText(manifestPath),
                manifestPath
            );
            if (depManifest?.ImportPrefix is null || diag.HasErrors)
                continue;

            RegisterManifest(depManifest, depPath, paths, aliases);
        }
    }
}
