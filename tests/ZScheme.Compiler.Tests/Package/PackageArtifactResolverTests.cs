using Xunit;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

/// <summary>
///     Freshness only. The rebuild path runs a real compile and is covered end to end by the
///     package test scripts.
/// </summary>
public sealed class PackageArtifactResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "zs-artifact-" + Guid.NewGuid().ToString("N")[..8]
    );

    private string CacheDir => Path.Combine(_root, "cache");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private string WriteStdlib()
    {
        var dir = Path.Combine(_root, "stdlib");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(
            Path.Combine(dir, "package.zspkg"),
            """
            (package
              (name "zscheme-stdlib")
              (version "1.0.0")
              (import-prefix "stdlib")
              (sources (main "src")))
            """
        );
        File.WriteAllText(Path.Combine(dir, "src", "option.zs"), "(define (some x) x)");
        return dir;
    }

    private string WriteHttp()
    {
        var dir = Path.Combine(_root, "http");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(
            Path.Combine(dir, "package.zspkg"),
            """
            (package
              (name "zscheme-http")
              (version "1.0.0")
              (import-prefix "http")
              (sources (main "src"))
              (dependencies
                (zscheme
                  [stdlib :local "../stdlib"])))
            """
        );
        File.WriteAllText(Path.Combine(dir, "src", "http.zs"), "(define (get url) url)");
        return dir;
    }

    private ResolvedPackage Resolve(string packageDir)
    {
        var resolved = PackageDependencyResolver.TryResolvePackage(packageDir);
        Assert.NotNull(resolved);
        return resolved;
    }

    /// <summary>
    ///     Stores an artifact describing <paramref name="package" /> exactly as it is on disk right
    ///     now — which is what a build of it would have recorded.
    /// </summary>
    private PrecompiledPackage StoreArtifactFor(ResolvedPackage package, bool withFingerprint = true)
    {
        var cache = new PackageCacheManager(ZSchemePaths.GetPackageCacheRoot(CacheDir));
        cache.Store(
            package.Name,
            package.Version,
            [0x4d, 0x5a],
            new Dictionary<string, CompiledModule>(),
            package.Prefix,
            package.DefaultModule,
            dependencies: PackageDependencyResolver.ResolveDependencyIdentities(
                package.ZSchemeDeps,
                package.PackageDir
            ),
            inputFingerprint: withFingerprint
                ? PackageFingerprint.Compute(package.PackageDir, package.SourceDir)
                : null
        );

        var stored = cache.TryLoad(package.Name, package.Version);
        Assert.NotNull(stored);
        return stored;
    }

    [Fact]
    public void UntouchedSources_IsFresh()
    {
        WriteStdlib();
        var http = Resolve(WriteHttp());

        Assert.True(PackageArtifactResolver.IsFresh(StoreArtifactFor(http), http));
    }

    [Fact]
    public void ChangedOwnSource_IsStale()
    {
        WriteStdlib();
        var http = Resolve(WriteHttp());
        var artifact = StoreArtifactFor(http);

        File.WriteAllText(Path.Combine(http.PackageDir, "src", "http.zs"), "(define (get u) 1)");

        Assert.False(PackageArtifactResolver.IsFresh(artifact, http));
    }

    /// <summary>
    ///     The case an own-inputs hash alone cannot catch: http's sources are byte-identical, but
    ///     it was compiled against signatures stdlib no longer offers.
    /// </summary>
    [Fact]
    public void ChangedDependencySource_IsStale()
    {
        var stdlib = WriteStdlib();
        var http = Resolve(WriteHttp());
        var artifact = StoreArtifactFor(http);

        File.WriteAllText(Path.Combine(stdlib, "src", "option.zs"), "(define (some x y) x)");

        Assert.False(PackageArtifactResolver.IsFresh(artifact, http));
    }

    [Fact]
    public void AddedDependency_IsStale()
    {
        WriteStdlib();
        var httpDir = WriteHttp();
        var artifact = StoreArtifactFor(Resolve(httpDir));

        var zunit = Path.Combine(_root, "zunit");
        Directory.CreateDirectory(Path.Combine(zunit, "src"));
        File.WriteAllText(
            Path.Combine(zunit, "package.zspkg"),
            """
            (package
              (name "zscheme-zunit")
              (version "1.0.0")
              (import-prefix "zunit")
              (sources (main "src")))
            """
        );
        File.WriteAllText(Path.Combine(zunit, "src", "zunit.zs"), "(define (check x) x)");
        File.WriteAllText(
            Path.Combine(httpDir, "package.zspkg"),
            """
            (package
              (name "zscheme-http")
              (version "1.0.0")
              (import-prefix "http")
              (sources (main "src"))
              (dependencies
                (zscheme
                  [stdlib :local "../stdlib"]
                  [zunit :local "../zunit"])))
            """
        );

        Assert.False(PackageArtifactResolver.IsFresh(artifact, Resolve(httpDir)));
    }

    /// <summary>
    ///     An artifact from before fingerprints were recorded cannot vouch for itself, so the first
    ///     build after an upgrade re-establishes the invariant instead of inheriting one it cannot
    ///     check.
    /// </summary>
    [Fact]
    public void ArtifactWithoutFingerprint_IsStale()
    {
        WriteStdlib();
        var http = Resolve(WriteHttp());

        Assert.False(
            PackageArtifactResolver.IsFresh(StoreArtifactFor(http, withFingerprint: false), http)
        );
    }

    [Fact]
    public void PackageWithNoManifestIdentity_ResolvesToNothing()
    {
        WriteStdlib();
        var http = Resolve(WriteHttp()) with { Name = "", Version = "" };

        Assert.Null(PackageArtifactResolver.Resolve(http, new DiagnosticBag(), CacheDir));
    }
}
