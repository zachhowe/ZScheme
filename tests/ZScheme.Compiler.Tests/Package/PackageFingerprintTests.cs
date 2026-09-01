using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

public sealed class PackageFingerprintTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "zs-fingerprint-" + Guid.NewGuid().ToString("N")[..8]
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private string NewPackage(string name, string moduleBody = "(define (f) 1)")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(
            Path.Combine(dir, "package.zspkg"),
            $"""
            (package
              (name "zscheme-{name}")
              (version "1.0.0")
              (import-prefix "{name}")
              (sources (main "src")))
            """
        );
        File.WriteAllText(Path.Combine(dir, "src", "core.zs"), moduleBody);
        return dir;
    }

    private static PackageManifest Parse(string packageDir)
    {
        var diagnostics = new DiagnosticBag();
        var manifestPath = Path.Combine(packageDir, "package.zspkg");
        var manifest = new ManifestParser(diagnostics).Parse(
            File.ReadAllText(manifestPath),
            manifestPath
        );
        Assert.False(diagnostics.HasErrors);
        return manifest!;
    }

    [Fact]
    public void SameInputs_SameFingerprint()
    {
        var dir = NewPackage("a");
        var manifest = Parse(dir);

        Assert.Equal(
            PackageFingerprint.Compute(dir, manifest),
            PackageFingerprint.Compute(dir, manifest)
        );
    }

    /// <summary>
    ///     The point of hashing content rather than comparing modification times: a checkout
    ///     rewrites mtimes on files whose bytes never changed.
    /// </summary>
    [Fact]
    public void TouchedButUnchangedSource_SameFingerprint()
    {
        var dir = NewPackage("a");
        var manifest = Parse(dir);
        var before = PackageFingerprint.Compute(dir, manifest);

        File.SetLastWriteTimeUtc(Path.Combine(dir, "src", "core.zs"), DateTime.UtcNow.AddHours(1));

        Assert.Equal(before, PackageFingerprint.Compute(dir, manifest));
    }

    [Fact]
    public void ChangedSource_ChangesFingerprint()
    {
        var dir = NewPackage("a");
        var manifest = Parse(dir);
        var before = PackageFingerprint.Compute(dir, manifest);

        File.WriteAllText(Path.Combine(dir, "src", "core.zs"), "(define (f) 2)");

        Assert.NotEqual(before, PackageFingerprint.Compute(dir, manifest));
    }

    [Fact]
    public void AddedSource_ChangesFingerprint()
    {
        var dir = NewPackage("a");
        var manifest = Parse(dir);
        var before = PackageFingerprint.Compute(dir, manifest);

        File.WriteAllText(Path.Combine(dir, "src", "extra.zs"), "(define (g) 1)");

        Assert.NotEqual(before, PackageFingerprint.Compute(dir, manifest));
    }

    /// <summary>
    ///     Same bytes under a different module name is a different package: the module a consumer
    ///     imports is named by its path.
    /// </summary>
    [Fact]
    public void RenamedSource_ChangesFingerprint()
    {
        var dir = NewPackage("a");
        var manifest = Parse(dir);
        var before = PackageFingerprint.Compute(dir, manifest);

        File.Move(Path.Combine(dir, "src", "core.zs"), Path.Combine(dir, "src", "other.zs"));

        Assert.NotEqual(before, PackageFingerprint.Compute(dir, manifest));
    }

    [Fact]
    public void ChangedManifest_ChangesFingerprint()
    {
        var dir = NewPackage("a");
        var manifest = Parse(dir);
        var before = PackageFingerprint.Compute(dir, manifest);

        File.AppendAllText(Path.Combine(dir, "package.zspkg"), "\n; a comment\n");

        Assert.NotEqual(before, PackageFingerprint.Compute(dir, manifest));
    }

    [Fact]
    public void DifferentPackagesWithIdenticalSources_DifferOnlyByManifest()
    {
        var a = NewPackage("a");
        var b = NewPackage("b");

        // The manifests differ (name, import-prefix), so the fingerprints must too, even though
        // both hold a byte-identical src/core.zs.
        Assert.NotEqual(
            PackageFingerprint.Compute(a, Parse(a)),
            PackageFingerprint.Compute(b, Parse(b))
        );
    }

    [Fact]
    public void MissingSourceDirectoryAndManifest_ReturnsNull()
    {
        var dir = NewPackage("a");
        var manifest = Parse(dir);
        Directory.Delete(Path.Combine(dir, "src"), true);
        File.Delete(Path.Combine(dir, "package.zspkg"));

        Assert.Null(PackageFingerprint.Compute(dir, manifest));
    }
}
