using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Package.NuGet;

namespace ZScript.Compiler.Package;

public sealed class NuGetResolver(DiagnosticBag diagnostics)
{
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zscript", "cache", "nuget");

    private static readonly string PackageCacheRoot = Path.Combine(CacheRoot, "packages");

    public string? Resolve(IReadOnlyList<NuGetDependency> packages)
    {
        if (packages.Count == 0)
            return null;

        var cacheKey = ComputeCacheKey(packages);
        var cacheDir = Path.Combine(CacheRoot, cacheKey);
        var outputDir = Path.Combine(cacheDir, "bin");

        Log.Debug("NuGetResolver: resolving {PackageCount} packages, cacheKey={CacheKey}", packages.Count, cacheKey);

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
        Log.Debug("NuGetResolver: dependency resolution completed in {ElapsedMs}ms, {PackageCount} packages resolved",
            sw.ElapsedMilliseconds, resolved.Count);
        if (diagnostics.HasErrors)
            return null;

        foreach (var pkg in resolved)
        {
            var dlls = NupkgExtractor.ExtractDlls(pkg.NupkgPath, outputDir);
            Log.Debug("NuGetResolver: extracted {DllCount} DLLs from {PackageId} {Version}", dlls.Count, pkg.Id, pkg.Version);
            if (dlls.Count == 0)
                diagnostics.Warning($"No compatible DLLs found in {pkg.Id} {pkg.Version}", SourceSpan.None);
        }

        var totalDlls = Directory.GetFiles(outputDir, "*.dll").Length;
        if (totalDlls == 0)
        {
            diagnostics.Error("No DLLs resolved from NuGet packages", SourceSpan.None);
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
