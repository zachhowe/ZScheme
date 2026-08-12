using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class ZSchemeHomeTests
{
    private static readonly string DefaultHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zscheme"
    );

    [Fact]
    public void GetHome_NoOverrides_UsesUserProfile()
    {
        Assert.Equal(DefaultHome, ZSchemeHome.GetHome(explicitOverride: null, envValue: null));
    }

    [Fact]
    public void GetHome_EnvValue_UsedWhenNoExplicit()
    {
        var result = ZSchemeHome.GetHome(explicitOverride: null, envValue: "/tmp/from-env");

        Assert.Equal(Path.GetFullPath("/tmp/from-env"), result);
    }

    [Fact]
    public void GetHome_ExplicitOverride_BeatsEnvValue()
    {
        var result = ZSchemeHome.GetHome("/tmp/explicit", "/tmp/from-env");

        Assert.Equal(Path.GetFullPath("/tmp/explicit"), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetHome_BlankValues_FallThrough(string blank)
    {
        Assert.Equal(DefaultHome, ZSchemeHome.GetHome(blank, blank));
    }

    [Fact]
    public void GetHome_ExpandsTilde()
    {
        var expected = Path.GetFullPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "zs")
        );

        Assert.Equal(expected, ZSchemeHome.GetHome(explicitOverride: null, envValue: "~/zs"));
    }

    [Fact]
    public void LayoutMembers_AreRootedAtTheGivenHome()
    {
        var home = Path.GetFullPath("/tmp/zshome");

        Assert.Equal(Path.Combine(home, "bin"), ZSchemeHome.GetBinDir(home));
        Assert.Equal(Path.Combine(home, "toolchains"), ZSchemeHome.GetToolchainsDir(home));
        Assert.Equal(
            Path.Combine(home, "toolchains", "0.4.0"),
            ZSchemeHome.GetToolchainDir("0.4.0", home)
        );
        Assert.Equal(
            Path.Combine(home, "toolchains", "dev.link"),
            ZSchemeHome.GetToolchainLinkFile("dev", home)
        );
        Assert.Equal(Path.Combine(home, "settings.json"), ZSchemeHome.GetSettingsFile(home));
        Assert.Equal(Path.Combine(home, "downloads"), ZSchemeHome.GetDownloadsDir(home));
        Assert.Equal(Path.Combine(home, "env"), ZSchemeHome.GetEnvFile(home));
        Assert.Equal(Path.Combine(home, "env.fish"), ZSchemeHome.GetEnvFishFile(home));
        Assert.Equal(Path.Combine(home, "cache"), ZSchemeHome.GetCacheRoot(home));
        Assert.Equal(Path.Combine(home, "cache", "nuget"), ZSchemeHome.GetNuGetCacheRoot(home));
    }

    [Fact]
    public void GetPackageCacheRootFor_IsKeyedByVersion()
    {
        var home = Path.GetFullPath("/tmp/zshome");

        Assert.Equal(
            Path.Combine(home, "cache", "pkg", "0.4.0"),
            ZSchemeHome.GetPackageCacheRootFor("0.4.0", home)
        );
        Assert.NotEqual(
            ZSchemeHome.GetPackageCacheRootFor("0.4.0", home),
            ZSchemeHome.GetPackageCacheRootFor("0.3.0", home)
        );
    }

    [Fact]
    public void GetLinkedCacheRoot_IsSeparateFromTheSharedCache()
    {
        var home = Path.GetFullPath("/tmp/zshome");
        var linked = ZSchemeHome.GetLinkedCacheRoot("dev", home);

        Assert.Equal(Path.Combine(home, "cache-dev", "dev"), linked);
        // Must not sit *inside* the shared cache, or a linked build could still poison it.
        Assert.DoesNotContain(ZSchemeHome.GetCacheRoot(home) + Path.DirectorySeparatorChar, linked);
    }

    [Fact]
    public void GetToolchainDir_RejectsNamesThatWouldEscapeTheRoot()
    {
        var home = Path.GetFullPath("/tmp/zshome");

        Assert.Throws<ArgumentException>(() => ZSchemeHome.GetToolchainDir("..", home));
        Assert.Throws<ArgumentException>(() => ZSchemeHome.GetToolchainDir("../../etc", home));
        Assert.Throws<ArgumentException>(() => ZSchemeHome.GetToolchainLinkFile("../evil", home));
    }

    [Fact]
    public void ExeName_AddsExtensionOnWindowsOnly()
    {
        var expected = OperatingSystem.IsWindows() ? "zs.exe" : "zs";

        Assert.Equal(expected, ZSchemeHome.ExeName("zs"));
    }
}
