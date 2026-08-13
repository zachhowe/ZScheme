using Xunit;

namespace ZScheme.Toolchain.Tests;

/// <summary>
///     These assert the exact wording. The messages are the whole user experience when a pin is
///     wrong, so a change to them should be a deliberate edit here rather than a silent drift.
/// </summary>
public sealed class ResolutionErrorFormatterTests
{
    private static string[] Lines(ToolchainResolution resolution)
    {
        return ResolutionErrorFormatter
            .Format(resolution)
            .Split(Environment.NewLine, StringSplitOptions.None);
    }

    [Fact]
    public void NotInstalled_FromProjectFile_NamesThePinFile()
    {
        var lines = Lines(
            new ToolchainResolution.NotInstalled(
                "0.4.0",
                ToolchainOrigin.ProjectFile,
                "/proj/.zscheme-version"
            )
        );

        Assert.Equal(
            [
                "error: toolchain '0.4.0' is not installed",
                "note: required by /proj/.zscheme-version",
                "help: run `zsup install 0.4.0`",
            ],
            lines
        );
    }

    [Fact]
    public void NotInstalled_FromEnvironment_OffersToUnsetIt()
    {
        var lines = Lines(
            new ToolchainResolution.NotInstalled(
                "0.4.0",
                ToolchainOrigin.EnvironmentVariable,
                OriginDetail: null
            )
        );

        Assert.Equal(
            [
                "error: toolchain '0.4.0' is not installed",
                "note: selected by ZSCHEME_VERSION",
                "help: run `zsup install 0.4.0`, or unset ZSCHEME_VERSION",
            ],
            lines
        );
    }

    [Fact]
    public void NotInstalled_FromGlobalDefault_SaysDefault()
    {
        var lines = Lines(
            new ToolchainResolution.NotInstalled(
                "0.4.0",
                ToolchainOrigin.GlobalDefault,
                OriginDetail: null
            )
        );

        Assert.Equal(
            [
                "error: the default toolchain '0.4.0' is not installed",
                "help: run `zsup install 0.4.0`, or `zsup use <toolchain>` to select another",
            ],
            lines
        );
    }

    [Fact]
    public void LinkBroken_NamesTheMissingTarget()
    {
        var lines = Lines(new ToolchainResolution.LinkBroken("dev", "/repos/ZScheme"));

        Assert.Equal(
            [
                "error: linked toolchain 'dev' points at /repos/ZScheme, which no longer exists",
                "help: run `zsup unlink dev`, or `zsup link dev <dir>` to re-point it",
            ],
            lines
        );
    }

    [Fact]
    public void NoToolchains_PointsAtInstall()
    {
        var lines = Lines(new ToolchainResolution.NoToolchains());

        Assert.Equal(
            ["error: no ZScheme toolchain is installed", "help: run `zsup install latest`"],
            lines
        );
    }

    /// <summary>
    ///     Never "no toolchain is installed": the toolchains are right there, and only a default is
    ///     missing. With one installed, the help line names it rather than asking for a choice.
    /// </summary>
    [Fact]
    public void NoDefault_WithOneToolchain_NamesIt()
    {
        var lines = Lines(new ToolchainResolution.NoDefault(["0.4.0"]));

        Assert.Equal(
            ["error: no default toolchain is selected", "help: run `zsup use 0.4.0`"],
            lines
        );
    }

    [Fact]
    public void NoDefault_WithSeveral_PointsAtTheList()
    {
        var lines = Lines(new ToolchainResolution.NoDefault(["0.3.0", "0.4.0"]));

        Assert.Equal(
            [
                "error: no default toolchain is selected",
                "help: run `zsup use <toolchain>`; `zsup list` shows what is installed",
            ],
            lines
        );
    }

    [Fact]
    public void Format_ResolvedIsACallerBug()
    {
        var resolved = new ToolchainResolution.Resolved(
            new InstalledToolchain(
                "0.4.0",
                "/tc",
                "/tc/bin",
                IsLinked: false,
                LinkTargetPath: null
            ),
            ToolchainOrigin.GlobalDefault,
            OriginDetail: null
        );

        Assert.Throws<ArgumentOutOfRangeException>(() => ResolutionErrorFormatter.Format(resolved));
    }
}
