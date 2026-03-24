using Xunit;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Modules;

namespace ZScript.Compiler.Tests.Modules;

public class ModuleResolverTests
{
    [Fact]
    public void ResolvesModule_FromSearchPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "mymod.zs"), "(define (f x) x)");

            var diag = new DiagnosticBag();
            var resolver = new ModuleResolver(diag);
            resolver.AddSearchPath(dir);

            var result = resolver.Resolve("mymod");

            Assert.NotNull(result);
            Assert.Contains("mymod.zs", result!.Value.Path);
            Assert.Contains("define", result.Value.Source);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReturnsNull_AndReportsError_WhenModuleNotFound()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var diag = new DiagnosticBag();
            var resolver = new ModuleResolver(diag);
            resolver.AddSearchPath(dir);

            var result = resolver.Resolve("nonexistent");

            Assert.Null(result);
            Assert.True(diag.HasErrors);
            Assert.Contains(diag.Diagnostics, d => d.Message.Contains("nonexistent"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void IgnoresNonExistentSearchPaths()
    {
        var diag = new DiagnosticBag();
        var resolver = new ModuleResolver(diag);
        resolver.AddSearchPath("/this/path/does/not/exist");

        var result = resolver.Resolve("anything");

        Assert.Null(result);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void ModuleNameWithSlashes_MapsToDirectorySeparators()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        var subDir = Path.Combine(dir, "sub");
        Directory.CreateDirectory(subDir);
        try
        {
            File.WriteAllText(Path.Combine(subDir, "nested.zs"), "(define (g) 1)");

            var diag = new DiagnosticBag();
            var resolver = new ModuleResolver(diag);
            resolver.AddSearchPath(dir);

            var result = resolver.Resolve("sub/nested");

            Assert.NotNull(result);
            Assert.Contains("nested.zs", result!.Value.Path);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
