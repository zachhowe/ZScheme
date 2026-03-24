namespace ZScript.Compiler.Tests.Package;

using ZScript.Compiler.Package.NuGet;
using Xunit;

public class TfmSelectorTests
{
    [Fact]
    public void SelectsNet10_WhenAvailable()
    {
        var result = TfmSelector.SelectBestTfm(["net6.0", "net8.0", "net10.0", "netstandard2.0"]);
        Assert.Equal("net10.0", result);
    }

    [Fact]
    public void FallsBackToNet9()
    {
        var result = TfmSelector.SelectBestTfm(["net6.0", "net9.0", "netstandard2.0"]);
        Assert.Equal("net9.0", result);
    }

    [Fact]
    public void FallsBackToNetStandard()
    {
        var result = TfmSelector.SelectBestTfm(["netstandard2.0", "net45"]);
        Assert.Equal("netstandard2.0", result);
    }

    [Fact]
    public void ReturnsNull_WhenNoCompatibleTfm()
    {
        var result = TfmSelector.SelectBestTfm(["net45", "net451"]);
        Assert.Null(result);
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        var result = TfmSelector.SelectBestTfm(["NET8.0", "NetStandard2.1"]);
        Assert.NotNull(result);
        Assert.Equal("net8.0", result, ignoreCase: true);
    }

    [Fact]
    public void ReturnsNull_WhenEmpty()
    {
        var result = TfmSelector.SelectBestTfm([]);
        Assert.Null(result);
    }
}
