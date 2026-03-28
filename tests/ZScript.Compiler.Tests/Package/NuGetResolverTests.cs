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
}
