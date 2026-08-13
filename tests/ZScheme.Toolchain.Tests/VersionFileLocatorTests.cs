using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class VersionFileLocatorTests
{
    [Fact]
    public void Find_NoPinAnywhere_ReturnsNull()
    {
        using var home = new TempHome();
        var deep = home.Dir("a", "b", "c");

        Assert.Null(VersionFileLocator.Find(deep));
    }

    [Fact]
    public void Find_NoStartDir_ReturnsNull()
    {
        // What a caller passes when getcwd failed -- on Unix the working directory can be unlinked
        // while a process still runs in it. Indistinguishable from "no pin found".
        Assert.Null(VersionFileLocator.Find(null));
        Assert.Null(VersionFileLocator.Find(""));
    }

    [Fact]
    public void Find_PinInStartDir_IsFound()
    {
        using var home = new TempHome();
        var dir = home.Dir("proj");
        var pin = Path.Combine(dir, ZSchemeHome.VersionFileName);
        File.WriteAllText(pin, "0.4.0");

        var hit = VersionFileLocator.Find(dir);

        Assert.NotNull(hit);
        Assert.Equal("0.4.0", hit.ToolchainName);
        Assert.Equal(pin, hit.FilePath);
    }

    [Fact]
    public void Find_WalksUpToAnAncestor()
    {
        using var home = new TempHome();
        var root = home.Dir("proj");
        var deep = home.Dir("proj", "src", "nested", "deeper");
        File.WriteAllText(Path.Combine(root, ZSchemeHome.VersionFileName), "0.3.0");

        var hit = VersionFileLocator.Find(deep);

        Assert.NotNull(hit);
        Assert.Equal("0.3.0", hit.ToolchainName);
    }

    [Fact]
    public void Find_NearestPinWins()
    {
        using var home = new TempHome();
        var outer = home.Dir("proj");
        var inner = home.Dir("proj", "sub");
        File.WriteAllText(Path.Combine(outer, ZSchemeHome.VersionFileName), "0.3.0");
        File.WriteAllText(Path.Combine(inner, ZSchemeHome.VersionFileName), "0.4.0");

        Assert.Equal("0.4.0", VersionFileLocator.Find(inner)!.ToolchainName);
        Assert.Equal("0.3.0", VersionFileLocator.Find(outer)!.ToolchainName);
    }

    [Fact]
    public void Find_DeepNesting_IsNotDepthLimited()
    {
        using var home = new TempHome();
        var root = home.Dir("proj");
        File.WriteAllText(Path.Combine(root, ZSchemeHome.VersionFileName), "0.4.0");

        // Well past the 10-level cap the package auto-installer's scan uses.
        var segments = Enumerable.Repeat("n", 15).ToArray();
        var deep = home.Dir([.. new[] { "proj" }.Concat(segments)]);

        Assert.Equal("0.4.0", VersionFileLocator.Find(deep)!.ToolchainName);
    }

    [Theory]
    [InlineData("0.4.0", "0.4.0")]
    [InlineData("  0.4.0  ", "0.4.0")]
    [InlineData("0.4.0\r\n", "0.4.0")]
    [InlineData("\n\n0.4.0\n", "0.4.0")]
    [InlineData("# a comment\n0.4.0\n", "0.4.0")]
    [InlineData("0.4.0\ntrailing junk\n", "0.4.0")]
    public void ReadToolchainName_ParsesTheFirstMeaningfulLine(string content, string expected)
    {
        using var home = new TempHome();
        var pin = Path.Combine(home.Path, ZSchemeHome.VersionFileName);
        File.WriteAllText(pin, content);

        Assert.Equal(expected, VersionFileLocator.ReadToolchainName(pin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("# only a comment\n")]
    public void ReadToolchainName_NothingSelectable_ReturnsNull(string content)
    {
        using var home = new TempHome();
        var pin = Path.Combine(home.Path, ZSchemeHome.VersionFileName);
        File.WriteAllText(pin, content);

        Assert.Null(VersionFileLocator.ReadToolchainName(pin));
    }

    [Fact]
    public void ReadToolchainName_StripsControlCharacters()
    {
        // The pin file is attacker-controlled -- it arrives in whatever repository was cloned --
        // and an unresolvable value gets echoed back to the terminal. Escape sequences must not
        // survive that round trip.
        using var home = new TempHome();
        var pin = Path.Combine(home.Path, ZSchemeHome.VersionFileName);
        File.WriteAllText(pin, "0.4.0]0;pwned");

        Assert.Equal("0.4.0]0;pwned", VersionFileLocator.ReadToolchainName(pin));
    }

    [Fact]
    public void ReadToolchainName_NothingButControlCharacters_ReturnsNull()
    {
        // Sanitizing strips every character here. An empty name is not a toolchain, and returning
        // one would resolve to `toolchain '' is not installed` on every command run from this
        // directory instead of falling back to the default.
        using var home = new TempHome();
        var pin = Path.Combine(home.Path, ZSchemeHome.VersionFileName);
        File.WriteAllText(pin, "\n");

        Assert.Null(VersionFileLocator.ReadToolchainName(pin));
    }

    [Fact]
    public void Find_PinOfOnlyControlCharacters_KeepsWalkingUp()
    {
        using var home = new TempHome();
        var outer = home.Dir("proj");
        var inner = home.Dir("proj", "sub");
        File.WriteAllText(Path.Combine(outer, ZSchemeHome.VersionFileName), "0.3.0");
        File.WriteAllText(Path.Combine(inner, ZSchemeHome.VersionFileName), "\n");

        Assert.Equal("0.3.0", VersionFileLocator.Find(inner)!.ToolchainName);
    }

    [Fact]
    public void ReadToolchainName_TruncatesAnAbsurdlyLongValue()
    {
        using var home = new TempHome();
        var pin = Path.Combine(home.Path, ZSchemeHome.VersionFileName);
        File.WriteAllText(pin, new string('x', 5000));

        var name = VersionFileLocator.ReadToolchainName(pin);

        Assert.NotNull(name);
        Assert.True(name.Length <= 64, $"expected a bounded name, got {name.Length} chars");
    }

    [Fact]
    public void Find_EmptyPinFile_KeepsWalkingUp()
    {
        using var home = new TempHome();
        var outer = home.Dir("proj");
        var inner = home.Dir("proj", "sub");
        File.WriteAllText(Path.Combine(outer, ZSchemeHome.VersionFileName), "0.3.0");
        File.WriteAllText(Path.Combine(inner, ZSchemeHome.VersionFileName), "# nothing here\n");

        Assert.Equal("0.3.0", VersionFileLocator.Find(inner)!.ToolchainName);
    }
}
