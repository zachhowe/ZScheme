using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

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
}
