using Xunit;
using ZScheme.Toolchain;
using ZScheme.Toolchain.Tests;

namespace ZScheme.Zsup.Tests;

public sealed class ZsupDoctorTests
{
    /// <summary>
    ///     The advisory runs after `zsup install` has committed, printed `installed toolchain
    ///     '...'` and recorded the default, and neither InstallCommand nor SelfCommand catches
    ///     anything it throws — so one over-long PATH entry turned a finished install into a bare
    ///     exception line and a non-zero exit that scripts read as a failed install.
    ///     PathTooLongException derives from IOException, not from the ArgumentException that was
    ///     caught.
    /// </summary>
    [Fact]
    public void WarnIfBinDirNotOnPath_ToleratesAnOverLongPathEntry()
    {
        using var home = new TempHome();
        var path = string.Join(
            Path.PathSeparator,
            new string('a', 33_000), // past what GetFullPath will parse
            ZSchemeHome.GetBinDir(home.Path)
        );

        Assert.Null(Record.Exception(() => ZsupDoctor.WarnIfBinDirNotOnPath(home.Path, path)));
    }

    [Fact]
    public void WarnIfBinDirNotOnPath_StillMatchesTheBinDirAmongJunkEntries()
    {
        // The unparseable entry must be skipped rather than turn the whole answer into "not on
        // PATH", which would warn about a PATH that is in fact correctly set up.
        using var home = new TempHome();
        var path = string.Join(
            Path.PathSeparator,
            new string('a', 33_000),
            ZSchemeHome.GetBinDir(home.Path) + Path.DirectorySeparatorChar
        );

        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            ZsupDoctor.WarnIfBinDirNotOnPath(home.Path, path);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal("", stderr.ToString());
    }
}
