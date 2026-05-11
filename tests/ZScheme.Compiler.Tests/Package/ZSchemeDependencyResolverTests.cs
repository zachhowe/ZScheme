using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

public class ZSchemeDependencyResolverTests
{
    [Fact]
    public void ResolvesLocalPath_Relative()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        var depDir = Path.Combine(tempDir, "my-lib");
        Directory.CreateDirectory(depDir);
        File.WriteAllText(Path.Combine(depDir, "lib.zs"), "(define (f x) x)");

        try
        {
            var diag = new DiagnosticBag();
            var resolver = new ZSchemeDependencyResolver(diag, tempDir);
            var deps = new List<ZSchemeDependency>
            {
                new("my-lib", new ZSchemeDependencySource.Local("my-lib"), SourceSpan.None)
            };

            var paths = resolver.Resolve(deps);

            Assert.Single(paths);
            Assert.Equal(Path.GetFullPath(depDir), paths[0]);
            Assert.False(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolvesLocalPath_Absolute()
    {
        var depDir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(depDir);
        File.WriteAllText(Path.Combine(depDir, "lib.zs"), "(define (f x) x)");

        var manifestDir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(manifestDir);

        try
        {
            var diag = new DiagnosticBag();
            var resolver = new ZSchemeDependencyResolver(diag, manifestDir);
            var deps = new List<ZSchemeDependency>
            {
                new("my-lib", new ZSchemeDependencySource.Local(depDir), SourceSpan.None)
            };

            var paths = resolver.Resolve(deps);

            Assert.Single(paths);
            Assert.Equal(Path.GetFullPath(depDir), paths[0]);
            Assert.False(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(depDir, true);
            Directory.Delete(manifestDir, true);
        }
    }

    [Fact]
    public void MissingLocalPath_ReportsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var diag = new DiagnosticBag();
            var resolver = new ZSchemeDependencyResolver(diag, tempDir);
            var deps = new List<ZSchemeDependency>
            {
                new("missing-lib", new ZSchemeDependencySource.Local("nonexistent"), SourceSpan.None)
            };

            var paths = resolver.Resolve(deps);

            Assert.Empty(paths);
            Assert.True(diag.HasErrors);
            Assert.Contains(diag.Diagnostics, d => d.Message.Contains("missing-lib"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReturnsEmpty_WhenNoDependencies()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var diag = new DiagnosticBag();
            var resolver = new ZSchemeDependencyResolver(diag, tempDir);

            var paths = resolver.Resolve([]);

            Assert.Empty(paths);
            Assert.False(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GitDependency_UsesOverrideCacheRoot()
    {
        // Pre-populate a fake git cache under an override root so Resolve returns it without
        // actually invoking git. This verifies the constructor's cacheRoot parameter is honored.
        var overrideRoot = Path.Combine(Path.GetTempPath(), $"zs_cache_{Guid.NewGuid():N}");
        var manifestDir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(manifestDir);

        const string url = "https://example.com/repo.git";
        const string versionOrRef = "v1.0.0";
        var urlHash = ComputeUrlHash(url);
        var expectedCacheDir = Path.Combine(overrideRoot, urlHash, versionOrRef);
        Directory.CreateDirectory(expectedCacheDir);
        File.WriteAllText(Path.Combine(expectedCacheDir, "lib.zs"), "(define (f x) x)");

        try
        {
            var diag = new DiagnosticBag();
            var resolver = new ZSchemeDependencyResolver(diag, manifestDir, overrideRoot);
            var deps = new List<ZSchemeDependency>
            {
                new("my-lib", new ZSchemeDependencySource.Git(url, versionOrRef), SourceSpan.None)
            };

            var paths = resolver.Resolve(deps);

            Assert.Single(paths);
            Assert.Equal(expectedCacheDir, paths[0]);
            Assert.False(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(overrideRoot, true);
            Directory.Delete(manifestDir, true);
        }
    }

    private static string ComputeUrlHash(string url)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
