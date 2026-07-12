using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

public class PackageDependencyResolverTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"zs_pkgdep_test_{Guid.NewGuid():N}"
    );

    public PackageDependencyResolverTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>Writes a package dir with a manifest (and an empty src/ dir) and returns its path.</summary>
    private string WritePackage(string dirName, string manifest)
    {
        var dir = Path.Combine(_tempDir, dirName);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "package.zspkg"), manifest);
        return dir;
    }

    private static ZSchemeDependency LocalDep(string name, string relPath) =>
        new(name, new ZSchemeDependencySource.Local(relPath), SourceSpan.None);

    // --- TryResolvePackage --------------------------------------------------------------

    [Fact]
    public void TryResolvePackageReturnsNullForMissingManifest()
    {
        var dir = Path.Combine(_tempDir, "bare");
        Directory.CreateDirectory(dir);

        Assert.Null(PackageDependencyResolver.TryResolvePackage(dir));
    }

    [Fact]
    public void TryResolvePackageReturnsNullWithoutImportPrefix()
    {
        var dir = WritePackage(
            "noprefix",
            """
            (package
              (name "no-prefix")
              (version "0.1.0"))
            """
        );

        Assert.Null(PackageDependencyResolver.TryResolvePackage(dir));
    }

    [Fact]
    public void TryResolvePackageReturnsNullForMalformedManifest()
    {
        var dir = WritePackage("malformed", "(package (name");

        Assert.Null(PackageDependencyResolver.TryResolvePackage(dir));
    }

    [Fact]
    public void TryResolvePackageResolvesPrefixSourceDirAndRefPaths()
    {
        var dir = WritePackage(
            "full",
            """
            (package
              (name "full-pkg")
              (version "0.1.0")
              (import-prefix "fp")
              (default-module "core")
              (sources (main "src"))
              (build (main (ref "libs"))))
            """
        );

        var resolved = PackageDependencyResolver.TryResolvePackage(dir);

        Assert.NotNull(resolved);
        Assert.Equal("fp", resolved.Prefix);
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), "src"), resolved.SourceDir);
        Assert.Equal("core", resolved.DefaultModule);
        Assert.Equal([Path.Combine(Path.GetFullPath(dir), "libs")], resolved.RefPaths);
    }

    [Fact]
    public void TryResolvePackageWithoutSourcesUsesPackageDirItself()
    {
        var dir = WritePackage(
            "flat",
            """
            (package
              (name "flat-pkg")
              (version "0.1.0")
              (import-prefix "flat"))
            """
        );

        var resolved = PackageDependencyResolver.TryResolvePackage(dir);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(dir), resolved.SourceDir);
    }

    // --- ResolveTransitiveClosure --------------------------------------------------------

    [Fact]
    public void ClosureFollowsDepOfDepRelativeToOwnerDir()
    {
        // root depends on ../a; A depends on ../b (relative to A, not to root).
        var aDir = WritePackage(
            "a",
            """
            (package
              (name "pkg-a")
              (version "0.1.0")
              (import-prefix "pa")
              (sources (main "src"))
              (dependencies (zscheme [pkg-b :local "../b"])))
            """
        );
        WritePackage(
            "b",
            """
            (package
              (name "pkg-b")
              (version "0.1.0")
              (import-prefix "pb")
              (sources (main "src")))
            """
        );
        var rootDir = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(rootDir);

        var diag = new DiagnosticBag();
        var closure = PackageDependencyResolver.ResolveTransitiveClosure(
            [LocalDep("pkg-a", "../a")],
            rootDir,
            diag
        );

        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.Equal(
            [Path.Combine(Path.GetFullPath(aDir), "src"), Path.Combine(_tempDir, "b", "src")],
            closure.ModuleSearchPaths
        );
        Assert.Equal(2, closure.PackagePaths.Count);
        Assert.True(closure.PackagePaths.ContainsKey("pa"));
        Assert.True(closure.PackagePaths.ContainsKey("pb"));
    }

    [Fact]
    public void DiamondDependencyIsVisitedOnce()
    {
        // root -> a -> shared, root -> b -> shared.
        WritePackage(
            "a",
            """
            (package
              (name "pkg-a")
              (version "0.1.0")
              (import-prefix "pa")
              (sources (main "src"))
              (dependencies (zscheme [pkg-shared :local "../shared"])))
            """
        );
        WritePackage(
            "b",
            """
            (package
              (name "pkg-b")
              (version "0.1.0")
              (import-prefix "pb")
              (sources (main "src"))
              (dependencies (zscheme [pkg-shared :local "../shared"])))
            """
        );
        WritePackage(
            "shared",
            """
            (package
              (name "pkg-shared")
              (version "0.1.0")
              (import-prefix "shared")
              (sources (main "src")))
            """
        );
        var rootDir = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(rootDir);

        var diag = new DiagnosticBag();
        var closure = PackageDependencyResolver.ResolveTransitiveClosure(
            [LocalDep("pkg-a", "../a"), LocalDep("pkg-b", "../b")],
            rootDir,
            diag
        );

        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        // a, b, and shared exactly once.
        Assert.Equal(3, closure.ModuleSearchPaths.Count);
        Assert.Single(closure.PackagePaths, kv => kv.Key == "shared");
    }

    [Fact]
    public void DirectDependencyWinsSharedPrefixOverTransitive()
    {
        // Both 'direct' and (transitively) 'shadowed' claim prefix "dup". BFS order
        // means the consumer's direct dep registers the prefix first.
        var directDir = WritePackage(
            "direct",
            """
            (package
              (name "pkg-direct")
              (version "0.1.0")
              (import-prefix "dup")
              (sources (main "src")))
            """
        );
        WritePackage(
            "carrier",
            """
            (package
              (name "pkg-carrier")
              (version "0.1.0")
              (import-prefix "carrier")
              (sources (main "src"))
              (dependencies (zscheme [pkg-shadowed :local "../shadowed"])))
            """
        );
        WritePackage(
            "shadowed",
            """
            (package
              (name "pkg-shadowed")
              (version "0.1.0")
              (import-prefix "dup")
              (sources (main "src")))
            """
        );
        var rootDir = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(rootDir);

        var diag = new DiagnosticBag();
        var closure = PackageDependencyResolver.ResolveTransitiveClosure(
            [LocalDep("pkg-carrier", "../carrier"), LocalDep("pkg-direct", "../direct")],
            rootDir,
            diag
        );

        Assert.Equal(
            Path.Combine(Path.GetFullPath(directDir), "src"),
            closure.PackagePaths["dup"]
        );
    }

    [Fact]
    public void BareDirectoryDependencyBecomesPlainSearchPath()
    {
        var bareDir = Path.Combine(_tempDir, "bare-dep");
        Directory.CreateDirectory(bareDir);
        var rootDir = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(rootDir);

        var diag = new DiagnosticBag();
        var closure = PackageDependencyResolver.ResolveTransitiveClosure(
            [LocalDep("bare-dep", "../bare-dep")],
            rootDir,
            diag
        );

        Assert.Equal([Path.GetFullPath(bareDir)], closure.ModuleSearchPaths);
        Assert.Empty(closure.PackagePaths);
    }

    [Fact]
    public void DefaultModuleProducesModuleAlias()
    {
        WritePackage(
            "aliased",
            """
            (package
              (name "pkg-aliased")
              (version "0.1.0")
              (import-prefix "al")
              (default-module "core")
              (sources (main "src")))
            """
        );
        var rootDir = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(rootDir);

        var diag = new DiagnosticBag();
        var closure = PackageDependencyResolver.ResolveTransitiveClosure(
            [LocalDep("pkg-aliased", "../aliased")],
            rootDir,
            diag
        );

        Assert.Equal("al/core", closure.ModuleAliases["al"]);
    }
}
