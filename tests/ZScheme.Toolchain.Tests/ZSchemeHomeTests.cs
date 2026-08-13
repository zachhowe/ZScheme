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
            ZSchemeHome.GetPackageCacheRootFor("0.4.0", home, cacheDirEnvValue: null)
        );
        Assert.NotEqual(
            ZSchemeHome.GetPackageCacheRootFor("0.4.0", home, cacheDirEnvValue: null),
            ZSchemeHome.GetPackageCacheRootFor("0.3.0", home, cacheDirEnvValue: null)
        );
    }

    /// <summary>
    ///     The variable has to outrank an explicitly passed home, unlike <c>ZSCHEME_HOME</c>. zsup
    ///     resolves the home first and passes it explicitly everywhere, so deferring to it would
    ///     leave <c>zsup install</c> seeding a directory no compile ever reads.
    /// </summary>
    [Fact]
    public void GetPackageCacheRootFor_CacheDirEnvValue_BeatsTheHome()
    {
        var home = Path.GetFullPath("/tmp/zshome");

        Assert.Equal(
            Path.Combine(Path.GetFullPath("/tmp/elsewhere"), "pkg", "0.4.0"),
            ZSchemeHome.GetPackageCacheRootFor("0.4.0", home, "/tmp/elsewhere")
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetEffectiveCacheRoot_BlankCacheDir_FallsBackToTheHome(string blank)
    {
        var home = Path.GetFullPath("/tmp/zshome");

        Assert.Equal(
            ZSchemeHome.GetCacheRoot(home),
            ZSchemeHome.GetEffectiveCacheRoot(home, blank)
        );
    }

    /// <summary>
    ///     The NuGet cache is the one that ignores <c>ZSCHEME_CACHE_DIR</c>, matching the compiler's
    ///     <c>NuGetResolver</c>.
    /// </summary>
    [Fact]
    public void GetNuGetCacheRoot_IgnoresTheCacheDirOverride()
    {
        var home = Path.GetFullPath("/tmp/zshome");

        Assert.Equal(Path.Combine(home, "cache", "nuget"), ZSchemeHome.GetNuGetCacheRoot(home));
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

    /// <summary>
    ///     The predicate behind the shim's self-dispatch guard. Spellings matter here because the
    ///     directory arrives from a hand-written <c>.link</c> file as often as from the layout.
    /// </summary>
    [Fact]
    public void IsBinDir_RecognizesTheHomesOwnBinDirectory()
    {
        var home = Path.GetFullPath("/tmp/zshome");
        var binDir = ZSchemeHome.GetBinDir(home);

        Assert.True(ZSchemeHome.IsBinDir(binDir, home));
        Assert.True(ZSchemeHome.IsBinDir(binDir + Path.DirectorySeparatorChar, home));
        Assert.True(ZSchemeHome.IsBinDir(Path.Combine(home, "toolchains", "..", "bin"), home));
    }

    [Fact]
    public void IsBinDir_RejectsEveryOtherDirectory()
    {
        var home = Path.GetFullPath("/tmp/zshome");

        Assert.False(ZSchemeHome.IsBinDir(home, home));
        Assert.False(ZSchemeHome.IsBinDir(Path.Combine(home, "bin", "nested"), home));
        Assert.False(
            ZSchemeHome.IsBinDir(
                Path.Combine(ZSchemeHome.GetToolchainDir("0.4.0", home), "bin"),
                home
            )
        );
        // Another home's bin directory holds its own shims, not this home's.
        Assert.False(
            ZSchemeHome.IsBinDir(ZSchemeHome.GetBinDir(Path.GetFullPath("/tmp/other")), home)
        );
    }

    /// <summary>
    ///     Answered rather than thrown: this runs on every <c>zs</c> invocation, and the caller's
    ///     "no zs there" path already covers a toolchain pointing somewhere unusable.
    /// </summary>
    [Fact]
    public void IsBinDir_UnparseablePath_IsNotThatDirectory()
    {
        Assert.False(ZSchemeHome.IsBinDir("", Path.GetFullPath("/tmp/zshome")));
    }

    [Fact]
    public void ExeName_AddsExtensionOnWindowsOnly()
    {
        var expected = OperatingSystem.IsWindows() ? "zs.exe" : "zs";

        Assert.Equal(expected, ZSchemeHome.ExeName("zs"));
    }
}
