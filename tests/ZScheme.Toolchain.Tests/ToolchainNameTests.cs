using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class ToolchainNameTests
{
    [Theory]
    [InlineData("0.4.0")]
    [InlineData("0.4.0-rc.1")]
    [InlineData("dev")]
    [InlineData("my_build")]
    [InlineData("feature-branch")]
    public void IsValid_AcceptsOrdinaryNames(string name)
    {
        Assert.True(ToolchainName.IsValid(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../etc")]
    [InlineData("../../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:")]
    [InlineData("C:\\Windows")]
    [InlineData(".hidden")]
    [InlineData(".staging-abc")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("wild*card")]
    [InlineData("pipe|name")]
    // Win32 strips a trailing dot from a path component, so `0.4.0.` and `0.4.0` resolve to one
    // directory while AreSame calls them two toolchains. Accepting it lets `zsup install 0.4.0.`
    // past ExplainNameTaken and straight over the installed `0.4.0` without --force, and lets
    // `zsup uninstall 0.4.0.` delete that directory while leaving `0.4.0` recorded as the default.
    [InlineData("0.4.0.")]
    [InlineData("dev.")]
    [InlineData("...")]
    public void IsValid_RejectsUnsafeOrAwkwardNames(string? name)
    {
        Assert.False(ToolchainName.IsValid(name));
    }

    [Fact]
    public void IsValid_RejectsControlCharacters()
    {
        Assert.False(ToolchainName.IsValid("bad\nname"));
        Assert.False(ToolchainName.IsValid("bad\0name"));
    }

    [Fact]
    public void Validate_ReturnsTheNameOrThrows()
    {
        Assert.Equal("0.4.0", ToolchainName.Validate("0.4.0"));
        Assert.Throws<ArgumentException>(() => ToolchainName.Validate("../escape"));
    }
}
