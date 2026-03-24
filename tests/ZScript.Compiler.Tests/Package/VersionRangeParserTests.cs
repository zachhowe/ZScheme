namespace ZScript.Compiler.Tests.Package;

using ZScript.Compiler.Package.NuGet;
using Xunit;

public class VersionRangeParserTests
{
    private static readonly IReadOnlyList<string> Versions =
        ["1.0.0", "1.1.0", "1.2.0", "2.0.0", "2.1.0", "3.0.0"];

    [Fact]
    public void BareVersion_SelectsHighestAboveMinimum()
    {
        var result = VersionRangeParser.FindBestMatch("1.0.0", Versions);
        Assert.Equal("3.0.0", result);
    }

    [Fact]
    public void ExactVersion_SelectsExact()
    {
        var result = VersionRangeParser.FindBestMatch("[1.2.0]", Versions);
        Assert.Equal("1.2.0", result);
    }

    [Fact]
    public void ExactVersion_ReturnsNull_WhenNotAvailable()
    {
        var result = VersionRangeParser.FindBestMatch("[1.3.0]", Versions);
        Assert.Null(result);
    }

    [Fact]
    public void MinimumInclusive_SelectsHighest()
    {
        var result = VersionRangeParser.FindBestMatch("[2.0.0, )", Versions);
        Assert.Equal("3.0.0", result);
    }

    [Fact]
    public void Range_SelectsHighestInRange()
    {
        var result = VersionRangeParser.FindBestMatch("[1.0.0, 2.0.0)", Versions);
        Assert.Equal("1.2.0", result);
    }

    [Fact]
    public void Range_InclusiveMax()
    {
        var result = VersionRangeParser.FindBestMatch("[1.0.0, 2.0.0]", Versions);
        Assert.Equal("2.0.0", result);
    }

    [Fact]
    public void ReturnsNull_WhenNoMatch()
    {
        var result = VersionRangeParser.FindBestMatch("[4.0.0, )", Versions);
        Assert.Null(result);
    }

    [Fact]
    public void HandlesPreReleaseVersions()
    {
        var versions = new List<string> { "1.0.0-beta1", "1.0.0", "1.1.0" };
        var result = VersionRangeParser.FindBestMatch("[1.0.0, )", versions);
        Assert.Equal("1.1.0", result);
    }
}
