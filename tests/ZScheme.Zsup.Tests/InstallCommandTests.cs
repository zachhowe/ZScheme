using Xunit;
using ZScheme.Toolchain;
using ZScheme.Toolchain.Tests;

namespace ZScheme.Zsup.Tests;

public sealed class InstallCommandTests
{
    /// <summary>
    ///     `--from` is the only option in the whole manager that takes a value, and its guard only
    ///     tested that a token existed. `zsup install --from --force 0.4.0` therefore swallowed
    ///     `--force` as the path and failed with "No such archive or directory: .../--force" --
    ///     naming something the user never typed, while every other option here rejects a stray
    ///     `-`-prefixed token.
    /// </summary>
    [Fact]
    public void Install_FromFollowedByAnOption_IsRejectedRatherThanSwallowingIt()
    {
        using var home = new TempHome();

        var result = ZsupProcess.Run(home.Path, ["install", "--from", "--force", "0.4.0"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--from needs a value", result.Stderr);
        Assert.DoesNotContain("No such archive", result.Stderr);
    }

    [Fact]
    public void Install_TrailingFrom_StillSaysItNeedsAValue()
    {
        using var home = new TempHome();

        // Not "unknown option: --from", which is what falling through to the default arm would say.
        var result = ZsupProcess.Run(home.Path, ["install", "0.4.0", "--from"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--from needs a value", result.Stderr);
        Assert.DoesNotContain("unknown option", result.Stderr);
    }

    [Fact]
    public void Install_FromADirectory_StillWorks()
    {
        using var home = new TempHome();
        var payload = home.Dir("payload", "bin");
        File.WriteAllText(Path.Combine(payload, ZSchemeHome.ExeName("zs")), "zs binary");
        File.WriteAllText(Path.Combine(payload, ZSchemeHome.ExeName("zs-lsp")), "lsp binary");

        var result = ZsupProcess.Run(
            home.Path,
            ["install", "dev", "--from", Path.Combine(home.Path, "payload")]
        );

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("installed toolchain 'dev'", result.Stdout);
    }
}
