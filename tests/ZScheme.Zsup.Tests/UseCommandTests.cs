using Xunit;
using ZScheme.Toolchain;
using ZScheme.Toolchain.Tests;

namespace ZScheme.Zsup.Tests;

public sealed class UseCommandTests
{
    [Fact]
    public void Use_AnInstalledToolchain_RecordsTheDefault()
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");

        var result = ZsupProcess.Run(home.Path, ["use", "0.4.0"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("0.4.0", result.Stdout);
        Assert.Equal("0.4.0", new ToolchainRegistry(home.Path).GetDefault());
    }

    /// <summary>
    ///     TryGet deliberately returns a link whose target is gone rather than null — callers are
    ///     documented as distinguishing that with IsLinkBroken, and `use` did not. It is the one
    ///     command whose entire job is selecting something usable, and it was the one command that
    ///     reported success here, leaving the home with a default every subsequent `zs` fails on.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the --local path returns before the checks below it, so it needs its own
    public void Use_ALinkWhoseTargetIsGone_IsRefused(bool local)
    {
        using var home = new TempHome();
        var target = home.Dir("build");
        home.AddLink("dev", target);
        Directory.Delete(target);

        var pinDir = home.Dir("project");
        string[] args = local ? ["use", "dev", "--local"] : ["use", "dev"];
        var result = ZsupProcess.Run(home.Path, args, workingDirectory: pinDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("no longer exists", result.Stderr);
        Assert.Contains("zsup unlink dev", result.Stderr);

        // Nothing selected, either globally or in the directory the command ran from.
        Assert.Null(new ToolchainRegistry(home.Path).GetDefault());
        Assert.False(File.Exists(Path.Combine(pinDir, ZSchemeHome.VersionFileName)));
    }

    [Fact]
    public void Use_AToolchainThatIsNotInstalled_IsRefused()
    {
        using var home = new TempHome();

        var result = ZsupProcess.Run(home.Path, ["use", "0.4.0"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("is not installed", result.Stderr);
    }
}
