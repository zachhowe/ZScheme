using Xunit;
using ZScheme.Compiler.Cache;

namespace ZScheme.Compiler.Tests.Cache;

[Collection("ZSchemePathsTests")]
public sealed class ZSchemePathsTests : IDisposable
{
    public ZSchemePathsTests()
    {
        ZSchemePaths.SetProcessDefaultCacheRoot(null);
    }

    public void Dispose()
    {
        ZSchemePaths.SetProcessDefaultCacheRoot(null);
    }

    [Fact]
    public void GetCacheRoot_NoOverrides_UsesUserProfileDefault()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zscheme", "cache");

        Assert.Equal(expected, ZSchemePaths.GetCacheRoot());
    }

    [Fact]
    public void GetCacheRoot_ExplicitOverride_TakesPrecedence()
    {
        ZSchemePaths.SetProcessDefaultCacheRoot("/tmp/process-default");

        var result = ZSchemePaths.GetCacheRoot("/tmp/explicit");

        Assert.Equal(Path.GetFullPath("/tmp/explicit"), result);
    }

    [Fact]
    public void GetCacheRoot_ProcessDefault_UsedWhenNoExplicit()
    {
        ZSchemePaths.SetProcessDefaultCacheRoot("/tmp/process-default");

        var result = ZSchemePaths.GetCacheRoot();

        Assert.Equal(Path.GetFullPath("/tmp/process-default"), result);
    }

    [Fact]
    public void GetCacheRoot_EmptyExplicit_FallsThroughToProcessDefault()
    {
        ZSchemePaths.SetProcessDefaultCacheRoot("/tmp/process-default");

        var result = ZSchemePaths.GetCacheRoot("");

        Assert.Equal(Path.GetFullPath("/tmp/process-default"), result);
    }

    [Fact]
    public void GetCacheRoot_WhitespaceExplicit_FallsThroughToProcessDefault()
    {
        ZSchemePaths.SetProcessDefaultCacheRoot("/tmp/process-default");

        var result = ZSchemePaths.GetCacheRoot("   ");

        Assert.Equal(Path.GetFullPath("/tmp/process-default"), result);
    }

    [Fact]
    public void SetProcessDefaultCacheRoot_Null_Clears()
    {
        ZSchemePaths.SetProcessDefaultCacheRoot("/tmp/something");
        ZSchemePaths.SetProcessDefaultCacheRoot(null);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zscheme", "cache");

        Assert.Equal(expected, ZSchemePaths.GetCacheRoot());
    }

    [Fact]
    public void SetProcessDefaultCacheRoot_Empty_Clears()
    {
        ZSchemePaths.SetProcessDefaultCacheRoot("/tmp/something");
        ZSchemePaths.SetProcessDefaultCacheRoot("");

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zscheme", "cache");

        Assert.Equal(expected, ZSchemePaths.GetCacheRoot());
    }

    [Fact]
    public void GetCacheRoot_RelativePath_ResolvedToAbsolute()
    {
        ZSchemePaths.SetProcessDefaultCacheRoot("relative/path");

        var result = ZSchemePaths.GetCacheRoot();

        Assert.True(Path.IsPathRooted(result));
        Assert.EndsWith(
            Path.Combine("relative", "path").TrimEnd(Path.DirectorySeparatorChar),
            result);
    }

    [Fact]
    public void GetCacheRoot_TildeExpansion_ResolvedToHome()
    {
        ZSchemePaths.SetProcessDefaultCacheRoot("~/my-cache");

        var result = ZSchemePaths.GetCacheRoot();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, "my-cache")), result);
    }

    [Fact]
    public void GetPackageCacheRoot_AppendsPkgAndVersion()
    {
        var result = ZSchemePaths.GetPackageCacheRoot("/tmp/override");

        Assert.Equal(
            Path.Combine(Path.GetFullPath("/tmp/override"), "pkg", CompilerInfo.BaseVersion),
            result);
    }

    [Fact]
    public void GetGitCacheRoot_AppendsGit()
    {
        var result = ZSchemePaths.GetGitCacheRoot("/tmp/override");

        Assert.Equal(
            Path.Combine(Path.GetFullPath("/tmp/override"), "git"),
            result);
    }

    [Fact]
    public void GetPackageCacheRoot_NoArg_UsesDefault()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zscheme", "cache", "pkg", CompilerInfo.BaseVersion);

        Assert.Equal(expected, ZSchemePaths.GetPackageCacheRoot());
    }
}
