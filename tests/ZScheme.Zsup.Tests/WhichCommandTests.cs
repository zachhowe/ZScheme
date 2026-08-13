using Xunit;
using ZScheme.Toolchain;
using ZScheme.Toolchain.Tests;

namespace ZScheme.Zsup.Tests;

public sealed class WhichCommandTests
{
    [Fact]
    public void Which_ResolvedTool_PrintsItsPathOnStdout()
    {
        using var home = new TempHome();
        var binDir = home.AddInstalled("0.4.0");
        new ToolchainRegistry(home.Path).SetDefault("0.4.0");

        var result = ZsupProcess.Run(home.Path, ["which", "zs"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            Path.Combine(binDir, ZSchemeHome.ExeName("zs")),
            result.Stdout.TrimEnd('\r', '\n')
        );
        Assert.Contains("default toolchain", result.Stderr);
    }

    /// <summary>
    ///     `$(zsup which zs-lsp)` is the documented way to point an editor at the language server,
    ///     and `Resolved` says nothing about the binary — only that a toolchain was selected and,
    ///     for a link, that its directory exists. Linking a CLI build output directory gives a
    ///     working `zs` and no `zs-lsp` at all, which is the case LinkCommand already warns about.
    /// </summary>
    [Fact]
    public void Which_AToolTheToolchainDoesNotHave_FailsAndPrintsNothingOnStdout()
    {
        using var home = new TempHome();
        // AddInstalled writes only `zs`, which is the shape a linked CLI build has.
        home.AddInstalled("0.4.0");
        new ToolchainRegistry(home.Path).SetDefault("0.4.0");

        var result = ZsupProcess.Run(home.Path, ["which", "zs-lsp"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Contains("has no zs-lsp", result.Stderr);
        Assert.Contains("expected it at", result.Stderr);
    }
}
