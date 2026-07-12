using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

/// <summary>
///     Tests <see cref="PackageAutoInstaller.TryAutoInstall" /> against a temp anchor dir and a
///     temp cache. Beware: when the anchor scan finds nothing, the installer falls back to
///     scanning up from the process CWD — which is inside this repo and finds the real
///     <c>packages/</c> — so "not found" cases must use names that exist nowhere, and
///     "found" cases must plant the source under the temp anchor.
/// </summary>
public class PackageAutoInstallerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"zs_autoinst_test_{Guid.NewGuid():N}"
    );

    private string AnchorDir => Path.Combine(_tempDir, "anchor");

    private string CacheDir => Path.Combine(_tempDir, "cache");

    public PackageAutoInstallerTests()
    {
        Directory.CreateDirectory(AnchorDir);
        Directory.CreateDirectory(CacheDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>Plants a compilable single-module package under anchor/packages/<paramref name="dirName" />.</summary>
    private string WritePackageSource(string dirName, string packageName)
    {
        var pkgDir = Path.Combine(AnchorDir, "packages", dirName);
        var srcDir = Path.Combine(pkgDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(
            Path.Combine(pkgDir, "package.zspkg"),
            $"""
            (package
              (name "{packageName}")
              (version "0.1.0")
              (import-prefix "{packageName}")
              (sources (main "src")))
            """
        );
        File.WriteAllText(
            Path.Combine(srcDir, "core.zs"),
            "(module core)\n(export answer)\n(define (answer) : Int 42)"
        );
        return pkgDir;
    }

    [Fact]
    public void UnknownPackageReturnsNull()
    {
        var diag = new DiagnosticBag();

        var result = PackageAutoInstaller.TryAutoInstall(
            $"zs-test-nonexistent-{Guid.NewGuid():N}",
            AnchorDir,
            diag,
            CacheDir
        );

        Assert.Null(result);
    }

    [Fact]
    public void SourceUnderAnchorIsCompiledCachedAndReturned()
    {
        WritePackageSource("mypkg", "zs-test-auto-pkg");
        var diag = new DiagnosticBag();

        var result = PackageAutoInstaller.TryAutoInstall(
            "zs-test-auto-pkg",
            AnchorDir,
            diag,
            CacheDir
        );

        Assert.NotNull(result);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        // Module keys are registered under the package's import prefix.
        Assert.Contains("zs-test-auto-pkg/core", result.Modules.Keys);
        // The compiled package landed in the temp cache.
        Assert.True(
            Directory
                .GetDirectories(CacheDir, "zs-test-auto-pkg", SearchOption.AllDirectories)
                .Length > 0
        );
    }

    [Fact]
    public void CacheHitShortCircuitsWithoutSource()
    {
        WritePackageSource("cachedpkg", "zs-test-cached-pkg");
        var diag = new DiagnosticBag();

        // First call compiles from source and populates the cache.
        var first = PackageAutoInstaller.TryAutoInstall(
            "zs-test-cached-pkg",
            AnchorDir,
            diag,
            CacheDir
        );
        Assert.NotNull(first);

        // Remove the source entirely; the second call must be served from the cache.
        Directory.Delete(Path.Combine(AnchorDir, "packages"), true);
        var second = PackageAutoInstaller.TryAutoInstall(
            "zs-test-cached-pkg",
            AnchorDir,
            diag,
            CacheDir
        );

        Assert.NotNull(second);
        Assert.Equal(first.Modules.Keys, second.Modules.Keys);
    }

    [Fact]
    public void BrokenPackageSourceReturnsNullWithDiagnostics()
    {
        var pkgDir = WritePackageSource("brokenpkg", "zs-test-broken-pkg");
        File.WriteAllText(
            Path.Combine(pkgDir, "src", "core.zs"),
            "(module core)\n(export answer)\n(define (answer) : String 42)"
        );
        var diag = new DiagnosticBag();

        var result = PackageAutoInstaller.TryAutoInstall(
            "zs-test-broken-pkg",
            AnchorDir,
            diag,
            CacheDir
        );

        Assert.Null(result);
        Assert.True(diag.HasErrors);
    }
}
