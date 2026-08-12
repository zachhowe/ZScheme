using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package.NuGet;
using ZScheme.Toolchain;

namespace ZScheme.Compiler.Package;

public sealed class NuGetResolver(DiagnosticBag diagnostics)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<NuGetResolver>();

    // Deliberately not routed through ZSchemePaths: the NuGet cache ignores ZSCHEME_CACHE_DIR. It
    // does follow ZSCHEME_HOME, so an isolated home (CI, e2e runs) does not write into the
    // developer's real ~/.zscheme.
    private static readonly string CacheRoot = ZSchemeHome.GetNuGetCacheRoot();

    private static readonly string PackageCacheRoot = Path.Combine(CacheRoot, "packages");

    public string? Resolve(IReadOnlyList<NuGetDependency> packages)
    {
        if (packages.Count == 0)
            return null;

        var cacheKey = ComputeCacheKey(packages);
        var cacheDir = Path.Combine(CacheRoot, cacheKey);
        var outputDir = Path.Combine(cacheDir, "bin");

        Log.Debug(
            "NuGetResolver: resolving {PackageCount} packages, cacheKey={CacheKey}",
            packages.Count,
            cacheKey
        );

        if (Directory.Exists(outputDir) && Directory.GetFiles(outputDir, "*.dll").Length > 0)
        {
            Log.Debug("NuGetResolver: cache hit at {OutputDir}", outputDir);
            return outputDir;
        }

        Log.Debug("NuGetResolver: cache miss, resolving packages from NuGet");
        Directory.CreateDirectory(outputDir);

        var sw = Stopwatch.StartNew();
        using var client = new NuGetV3Client();
        var graph = new NuGetDependencyGraph(client, PackageCacheRoot, diagnostics);

        var resolved = graph.ResolveAsync(packages).GetAwaiter().GetResult();
        Log.Debug(
            "NuGetResolver: dependency resolution completed in {ElapsedMs}ms, {PackageCount} packages resolved",
            sw.ElapsedMilliseconds,
            resolved.Count
        );
        if (diagnostics.HasErrors)
            return null;

        var spanLookup = packages.ToDictionary(
            p => p.PackageId,
            p => p.Span,
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var pkg in resolved)
        {
            var dlls = NupkgExtractor.ExtractDlls(pkg.NupkgPath, outputDir);
            Log.Debug(
                "NuGetResolver: extracted {DllCount} DLLs from {PackageId} {Version}",
                dlls.Count,
                pkg.Id,
                pkg.Version
            );
            if (dlls.Count == 0)
                diagnostics.Warning(
                    $"No compatible DLLs found in {pkg.Id} {pkg.Version}",
                    spanLookup.GetValueOrDefault(pkg.Id)
                );
        }

        var totalDlls = Directory.GetFiles(outputDir, "*.dll").Length;
        if (totalDlls == 0)
        {
            diagnostics.Error("No DLLs resolved from NuGet packages", packages[0].Span);
            return null;
        }

        Log.Debug("NuGetResolver: {DllCount} total DLLs in {OutputDir}", totalDlls, outputDir);
        return outputDir;
    }

    private static string ComputeCacheKey(IReadOnlyList<NuGetDependency> packages)
    {
        var sorted = packages.OrderBy(p => p.PackageId).ThenBy(p => p.Version);
        var input = string.Join(";", sorted.Select(p => $"{p.PackageId}={p.Version}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
