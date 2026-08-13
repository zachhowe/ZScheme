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

        var stamped = ShimInstaller.Install(binDir, zsup);

        Assert.Equal(ShimInstaller.ShimNames.Length, stamped.Written.Count);
        Assert.Empty(stamped.Failed);
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
    public void Install_StampsTheRemainingNamesWhenOneCannotBeWritten()
    {
        using var home = new TempHome();
        var zsup = MakeZsup(home);
        var binDir = ZSchemeHome.GetBinDir(home.Path);

        // A directory sitting on the first name is the portable stand-in for the real cause, a
        // Windows lock: the name cannot be written, and it is stamped before `zs-lsp`.
        var blocked = ShimInstaller.ShimNames[0];
        Directory.CreateDirectory(Path.Combine(binDir, ZSchemeHome.ExeName(blocked)));

        var stamped = ShimInstaller.Install(binDir, zsup);

        // The whole point: an unwritable `zs` must not stop `zs-lsp` from being brought up to date,
        // and the name that missed out has to come back named.
        var failure = Assert.Single(stamped.Failed);
        Assert.Equal(blocked, failure.Name);
        Assert.NotEmpty(failure.Message);
        foreach (var name in ShimInstaller.ShimNames.Where(n => n != blocked))
        {
            var path = Path.Combine(binDir, ZSchemeHome.ExeName(name));
            Assert.Contains(path, stamped.Written);
            Assert.Equal("zsup binary", File.ReadAllText(path));
        }
    }

    [Fact]
    public void Install_LeavesALockedShimInPlaceRatherThanDeletingIt()
    {
        // Only Windows refuses to replace an open file; on Unix the delete-and-relink below just
        // succeeds, which is the behaviour the hardlink handling needs.
        if (!OperatingSystem.IsWindows())
            return;

        using var home = new TempHome();
        var zsup = MakeZsup(home);
        var binDir = ZSchemeHome.GetBinDir(home.Path);
        ShimInstaller.Install(binDir, zsup);

        var locked = Path.Combine(binDir, ZSchemeHome.ExeName(ShimInstaller.ShimNames[0]));
        File.WriteAllText(zsup, "updated zsup");

        ShimInstaller.Result stamped;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) // as a running image holds it
        {
            stamped = ShimInstaller.Install(binDir, zsup);
        }

        // Stale is recoverable -- the user closes the editor and re-runs. Deleted is not: the old
        // shim would be gone and the new one never written.
        Assert.Single(stamped.Failed);
        Assert.Equal("zsup binary", File.ReadAllText(locked));
    }

    [Fact]
    public void Install_LeavesNoStagingFileBehind()
    {
        using var home = new TempHome();
        var zsup = MakeZsup(home);
        var binDir = ZSchemeHome.GetBinDir(home.Path);

        ShimInstaller.Install(binDir, zsup);

        // The Windows path stages beside the shim before renaming over it. Nothing else walks the
        // bin directory looking for those, so one left per stamp would pile up zsup-sized files.
        Assert.Empty(Directory.GetFiles(binDir, "*.tmp-*"));
    }

    [Fact]
    public void Install_SweepsAStagingFileAKilledRunLeftBehind()
    {
        // Only the Windows path stages; elsewhere the shim is hardlinked or symlinked in one step.
        if (!OperatingSystem.IsWindows())
            return;

        using var home = new TempHome();
        var zsup = MakeZsup(home);
        var binDir = ZSchemeHome.GetBinDir(home.Path);

        // The try/finally around the rename covers a failure but not a kill between the copy and
        // the rename, which is what leaves one of these.
        var abandoned = Path.Combine(binDir, ZSchemeHome.ExeName("zs") + ".tmp-deadbeef");
        File.WriteAllText(abandoned, "an interrupted stamp");
        File.SetLastWriteTimeUtc(
            abandoned,
            DateTime.UtcNow - ZSchemeHome.StagingMaxAge - TimeSpan.FromMinutes(1)
        );

        ShimInstaller.Install(binDir, zsup);

        Assert.False(File.Exists(abandoned));
        Assert.Equal(
            "zsup binary",
            File.ReadAllText(Path.Combine(binDir, ZSchemeHome.ExeName("zs")))
        );
    }

    [Fact]
    public void Install_LeavesAConcurrentInstallsStagingFileAlone()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var home = new TempHome();
        var zsup = MakeZsup(home);
        var binDir = ZSchemeHome.GetBinDir(home.Path);

        // A slot another zsup is stamping through right now: it copied into this a moment ago and
        // is about to rename it over `zs`. Sweeping it would leave that process renaming a path
        // that no longer exists -- reported as a shim it could not refresh, for a shim that ends up
        // perfectly current, with recovery advice naming a lock that does not exist.
        var live = Path.Combine(binDir, ZSchemeHome.ExeName("zs") + ".tmp-a1b2c3d4");
        File.WriteAllText(live, "another process mid-stamp");

        ShimInstaller.Install(binDir, zsup);

        Assert.True(File.Exists(live));
        Assert.Equal("another process mid-stamp", File.ReadAllText(live));
    }

    [Fact]
    public void MatchName_ReturnsTheCanonicalName()
    {
        Assert.Equal("zs", ShimInstaller.MatchName("zs"));
        Assert.Equal("zs-lsp", ShimInstaller.MatchName("zs-lsp"));
    }

    [Fact]
    public void MatchName_RejectsNamesThatAreNotShims()
    {
        Assert.Null(ShimInstaller.MatchName("zsup"));
        Assert.Null(ShimInstaller.MatchName(""));
        Assert.Null(ShimInstaller.MatchName(null));
    }

    [Fact]
    public void MatchName_MatchesTheWayTheFilesystemResolvesNames()
    {
        // Typing `ZS` on Windows launches zs.exe, and argv[0] then carries the case the user typed.
        // An ordinal match there would drop them into the manager CLI instead of the compiler.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Equal("zs", ShimInstaller.MatchName("ZS"));
        else
            Assert.Null(ShimInstaller.MatchName("ZS"));
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
