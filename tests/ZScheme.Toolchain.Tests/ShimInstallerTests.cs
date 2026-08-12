using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class ShimInstallerTests
{
    private static string MakeZsup(TempHome home)
    {
        var binDir = ZSchemeHome.GetBinDir(home.Path);
        Directory.CreateDirectory(binDir);
        var zsup = Path.Combine(binDir, ZSchemeHome.ExeName("zsup"));
        File.WriteAllText(zsup, "zsup binary");
        return zsup;
    }

    [Fact]
    public void Install_CreatesAShimForEveryName()
    {
        using var home = new TempHome();
        var zsup = MakeZsup(home);
        var binDir = ZSchemeHome.GetBinDir(home.Path);

        var written = ShimInstaller.Install(binDir, zsup);

        Assert.Equal(ShimInstaller.ShimNames.Length, written.Count);
        foreach (var name in ShimInstaller.ShimNames)
            Assert.True(File.Exists(Path.Combine(binDir, ZSchemeHome.ExeName(name))));
    }

    [Fact]
    public void Install_ShimsHaveTheSameContentAsZsup()
    {
        using var home = new TempHome();
        var zsup = MakeZsup(home);
        var binDir = ZSchemeHome.GetBinDir(home.Path);

        ShimInstaller.Install(binDir, zsup);

        // Whether the platform used a copy, a hardlink, or a symlink, reading through the shim
        // must yield the zsup binary -- that is what makes argv[0] dispatch work.
        foreach (var name in ShimInstaller.ShimNames)
            Assert.Equal(
                File.ReadAllText(zsup),
                File.ReadAllText(Path.Combine(binDir, ZSchemeHome.ExeName(name)))
            );
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        using var home = new TempHome();
        var zsup = MakeZsup(home);
        var binDir = ZSchemeHome.GetBinDir(home.Path);

        ShimInstaller.Install(binDir, zsup);
        ShimInstaller.Install(binDir, zsup);

        Assert.Equal(
            "zsup binary",
            File.ReadAllText(Path.Combine(binDir, ZSchemeHome.ExeName("zs")))
        );
    }

    [Fact]
    public void Install_RestampsAfterZsupChanges()
    {
        using var home = new TempHome();
        var zsup = MakeZsup(home);
        var binDir = ZSchemeHome.GetBinDir(home.Path);
        ShimInstaller.Install(binDir, zsup);

        // Rename-then-replace, exactly what `zsup self update` does on Windows. A stale hardlink
        // would still point at the old inode, which is why the shims are re-stamped afterwards.
        File.Move(zsup, zsup + ".old");
        File.WriteAllText(zsup, "updated zsup");
        ShimInstaller.Install(binDir, zsup);

        Assert.Equal(
            "updated zsup",
            File.ReadAllText(Path.Combine(binDir, ZSchemeHome.ExeName("zs")))
        );
    }

    [Fact]
    public void Install_CreatesTheBinDirIfAbsent()
    {
        using var home = new TempHome();
        var zsup = MakeZsup(home);
        var fresh = Path.Combine(home.Path, "elsewhere");

        ShimInstaller.Install(fresh, zsup);

        Assert.True(File.Exists(Path.Combine(fresh, ZSchemeHome.ExeName("zs"))));
    }

    [Fact]
    public void Install_ShimsAreExecutableOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var home = new TempHome();
        var zsup = MakeZsup(home);
        ShimInstaller.MakeExecutable(zsup);
        var binDir = ZSchemeHome.GetBinDir(home.Path);

        ShimInstaller.Install(binDir, zsup);

        foreach (var name in ShimInstaller.ShimNames)
        {
            var path = Path.Combine(binDir, name);
            // Follows a symlink to its target, which is the right thing either way.
            Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void MakeExecutable_IsSafeOnEveryPlatform()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "file");
        File.WriteAllText(path, "x");

        // Must not throw on Windows, where Unix file modes do not exist.
        ShimInstaller.MakeExecutable(path);
    }
}
