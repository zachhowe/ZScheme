using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class RuntimeIdentifierTests
{
    [Fact]
    public void Detect_ReturnsARidThisProjectPublishesFor()
    {
        // Tests only run on platforms releases are built for, so detection must succeed here.
        var rid = RuntimeIdentifier.Detect();

        Assert.Contains(rid, RuntimeIdentifier.Supported);
    }

    [Fact]
    public void Detect_MatchesTheHostOperatingSystem()
    {
        var rid = RuntimeIdentifier.Detect();

        var expectedPrefix =
            OperatingSystem.IsWindows() ? "win-"
            : OperatingSystem.IsMacOS() ? "osx-"
            : "linux-";

        Assert.StartsWith(expectedPrefix, rid);
    }

    [Theory]
    [InlineData("win-x64", ".zip")]
    [InlineData("win-arm64", ".zip")]
    [InlineData("linux-x64", ".tar.gz")]
    [InlineData("linux-arm64", ".tar.gz")]
    [InlineData("osx-x64", ".tar.gz")]
    [InlineData("osx-arm64", ".tar.gz")]
    public void ArchiveExtension_MatchesTheFormatPublishUses(string rid, string expected)
    {
        Assert.Equal(expected, RuntimeIdentifier.ArchiveExtension(rid));
    }

    [Fact]
    public void Supported_CoversBothArchitecturesOnEveryPlatform()
    {
        Assert.Equal(6, RuntimeIdentifier.Supported.Length);
        Assert.Equal(
            RuntimeIdentifier.Supported.Length,
            RuntimeIdentifier.Supported.Distinct().Count()
        );
    }
}
