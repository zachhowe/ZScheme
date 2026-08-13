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

    [Theory]
    [InlineData("0.4.0", "0.4.0")]
    [InlineData("  0.4.0  ", "0.4.0")]
    // An ANSI colour escape and an OSC title sequence. Both reach ResolutionErrorFormatter, which
    // prints the requested name straight back to the terminal when it is not installed.
    [InlineData("\u001b[31m0.4.0", "[31m0.4.0")]
    [InlineData("\u001b]0;pwned\u0007", "]0;pwned")]
    // The trim has to happen after the strip, or the spaces the escape sat between survive it.
    [InlineData(" \u001b[2J 0.4.0 ", "[2J 0.4.0")]
    public void Sanitize_StripsControlCharactersAndTrims(string value, string expected)
    {
        Assert.Equal(expected, ToolchainName.Sanitize(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u001b\u0007")]
    public void Sanitize_ReturnsNullWhenNothingSurvives(string? value)
    {
        // Not the empty string: that is not a toolchain name, and a caller taking it as one would
        // fail every command from that directory with "toolchain '' is not installed".
        Assert.Null(ToolchainName.Sanitize(value));
    }

    [Fact]
    public void Sanitize_BoundsTheLength()
    {
        Assert.Equal(64, ToolchainName.Sanitize(new string('x', 500))!.Length);
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
