using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class PathNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_NullOrBlank_ReturnsNull(string? value)
    {
        Assert.Null(PathNormalizer.Normalize(value));
    }

    [Fact]
    public void Normalize_RelativePath_BecomesAbsolute()
    {
        var result = PathNormalizer.Normalize("relative/path");

        Assert.NotNull(result);
        Assert.True(Path.IsPathRooted(result));
        Assert.EndsWith(Path.Combine("relative", "path"), result);
    }

    [Fact]
    public void Normalize_Tilde_ExpandsToUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.GetFullPath(home), PathNormalizer.Normalize("~"));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(home, "my-cache")),
            PathNormalizer.Normalize("~/my-cache")
        );
    }

    [Fact]
    public void Normalize_TildeNotFollowedBySeparator_IsNotExpanded()
    {
        // "~foo" is a literal directory name, not a home-relative path.
        var result = PathNormalizer.Normalize("~foo");

        Assert.NotNull(result);
        Assert.EndsWith("~foo", result);
    }

    [Fact]
    public void Normalize_TrimsSurroundingWhitespace()
    {
        Assert.Equal(
            PathNormalizer.Normalize("relative"),
            PathNormalizer.Normalize("  relative  ")
        );
    }
}
