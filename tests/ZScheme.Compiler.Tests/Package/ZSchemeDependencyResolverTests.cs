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
}
