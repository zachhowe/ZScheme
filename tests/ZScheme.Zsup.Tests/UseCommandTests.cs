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

    /// <summary>
    ///     The --local branch returned before the precedence warnings the global path prints, so
    ///     with ZSCHEME_VERSION exported -- direnv, a CI job, a devcontainer -- the pin was written,
    ///     simply outranked, and reported as a plain success. That is the outcome the warnings exist
    ///     to prevent, and the comment on them says so: it would otherwise look like the command did
    ///     nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Use_WithZSchemeVersionNamingSomethingElse_WarnsOnBothPaths(bool local)
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");
        home.AddInstalled("0.3.0");
        var workDir = home.Dir("project");

        string[] args = local ? ["use", "0.4.0", "--local"] : ["use", "0.4.0"];
        var result = ZsupProcess.Run(
            home.Path,
            args,
            new Dictionary<string, string?> { ["ZSCHEME_VERSION"] = "0.3.0" },
            workDir
        );

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ZSCHEME_VERSION is set to '0.3.0'", result.Stderr);
        Assert.Contains("takes precedence", result.Stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Use_WithZSchemeVersionNamingTheSameToolchain_StaysQuiet(bool local)
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");
        var workDir = home.Dir("project");

        string[] args = local ? ["use", "0.4.0", "--local"] : ["use", "0.4.0"];
        var result = ZsupProcess.Run(
            home.Path,
            args,
            new Dictionary<string, string?> { ["ZSCHEME_VERSION"] = "0.4.0" },
            workDir
        );

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public void Use_Local_DoesNotWarnAboutTheFileItJustWrote()
    {
        // The pin-file half of the warning is not shared with --local: the file just written in the
        // current directory is the nearest one by definition, so it cannot be outranked by another
        // pin, and a .zscheme-version in a subdirectory does not outrank it either.
        using var home = new TempHome();
        home.AddInstalled("0.4.0");
        var workDir = home.Dir("project");

        var result = ZsupProcess.Run(
            home.Path,
            ["use", "0.4.0", "--local"],
            workingDirectory: workDir
        );

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal(
            "0.4.0",
            VersionFileLocator.ReadToolchainName(Path.Combine(workDir, ZSchemeHome.VersionFileName))
        );
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
