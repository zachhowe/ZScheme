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
        var original = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                string.Join(
                    Path.PathSeparator,
                    new string('a', 33_000), // past what GetFullPath will parse
                    ZSchemeHome.GetBinDir(home.Path)
                )
            );

            Assert.Null(Record.Exception(() => ZsupDoctor.WarnIfBinDirNotOnPath(home.Path)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }
}
