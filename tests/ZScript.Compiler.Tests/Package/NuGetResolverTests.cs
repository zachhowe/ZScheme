using Xunit;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Package;

namespace ZScript.Compiler.Tests.Package;

public class NuGetResolverTests
{
    [Fact]
    public void ReturnsNull_WhenNoDependencies()
    {
        var diag = new DiagnosticBag();
        var resolver = new NuGetResolver(diag);

        var result = resolver.Resolve([]);

        Assert.Null(result);
        Assert.False(diag.HasErrors);
    }

    [Fact(Skip = "Integration test: requires network and dotnet CLI")]
    public void ResolvesKnownPackage()
    {
        var diag = new DiagnosticBag();
        var resolver = new NuGetResolver(diag);
        var deps = new List<NuGetDependency>
        {
            new("Newtonsoft.Json", "13.0.3", SourceSpan.None)
        };

        var outputDir = resolver.Resolve(deps);

        Assert.NotNull(outputDir);
        Assert.False(diag.HasErrors);
        Assert.True(Directory.Exists(outputDir));
        Assert.Contains(Directory.GetFiles(outputDir!, "*.dll"),
            f => Path.GetFileName(f).Contains("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase));
    }
}
