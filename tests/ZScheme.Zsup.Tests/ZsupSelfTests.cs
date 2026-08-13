using Xunit;
using ZScheme.Toolchain;
using ZScheme.Toolchain.Tests;

namespace ZScheme.Zsup.Tests;

public sealed class ZsupSelfTests
{
    private static readonly TimeSpan PastTheGate =
        ZSchemeHome.StagingMaxAge + TimeSpan.FromMinutes(5);

    [Fact]
    public void SweepStaleBinaries_TakesAbandonedSlots_AndLeavesEverythingElse()
    {
        using var home = new TempHome();
        var bin = home.Dir("bin");
        var zsup = ZSchemeHome.ExeName("zsup");

        var abandonedMoveAside = Stamp(Path.Combine(bin, zsup + ".old-1"), PastTheGate);
        var abandonedStaging = Stamp(Path.Combine(bin, zsup + ".new-1"), PastTheGate);
        var youngMoveAside = Stamp(Path.Combine(bin, zsup + ".old-2"), TimeSpan.Zero);
        var rescue = Stamp(Path.Combine(bin, zsup + ".rescue-1"), PastTheGate);
        var live = Stamp(Path.Combine(bin, zsup), TimeSpan.Zero);

        ZsupSelf.SweepStaleBinaries(home.Path);

        Assert.False(File.Exists(abandonedMoveAside));
        Assert.False(File.Exists(abandonedStaging));
        // Age-gated on purpose: a young slot belongs to a concurrent `zsup self update`, and the
        // moved-aside binary is the only copy a failed one has to put back.
        Assert.True(File.Exists(youngMoveAside));
        // A rescue copy is the last remaining copy of an installation the user still has to restore
        // by hand, so it sits outside what this deletes however old it gets.
        Assert.True(File.Exists(rescue));
        Assert.True(File.Exists(live));
    }

    [Fact]
    public void SweepStaleBinaries_UnreadableBinDir_DoesNotThrow()
    {
        using var home = new TempHome();
        var bin = home.Dir("bin");
        Stamp(Path.Combine(bin, ZSchemeHome.ExeName("zsup") + ".old-1"), PastTheGate);

        if (!TempHome.TryMakeDirectoryUnreadable(bin))
            return;

        try
        {
            // A bin/ left root-owned by a `sudo zsup install` costs the sweep and nothing else.
            // Program sweeps before dispatch with no handler above it, so throwing would take down
            // `zsup list`/`which`/`use`, none of which otherwise touch bin/ -- and
            // ReplaceInstalledBinaries sweeps *after* the new binaries are in place, where a throw
            // reports an update that fully succeeded as an error.
            ZsupSelf.SweepStaleBinaries(home.Path);
        }
        finally
        {
            TempHome.MakeDirectoryReadable(bin);
        }
    }

    [Fact]
    public void SweepStaleBinaries_MissingBinDir_IsNotAnError()
    {
        using var home = new TempHome();

        ZsupSelf.SweepStaleBinaries(home.Path);
    }

    private static string Stamp(string path, TimeSpan age)
    {
        File.WriteAllText(path, "binary");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }
}
