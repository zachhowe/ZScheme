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
