using Xunit;
using ZScheme.Toolchain.Tests;

namespace ZScheme.Zsup.Tests;

public sealed class SelfUninstallTests
{
    /// <summary>
    ///     The Windows refusal rests on "the running binary lives inside the tree being deleted",
    ///     which is a claim about this process and is only true when the running zsup is the one
    ///     installed under that home. This test binary is not — which is the situation every
    ///     toolchain test already runs in, and the situation a Windows CI job tearing down a
    ///     ZSCHEME_HOME scratch directory with a repo-built zsup is in.
    /// </summary>
    [Fact]
    public void SelfUninstall_AZsupOutsideTheHome_RemovesIt()
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");

        var result = ZsupProcess.Run(home.Path, ["self", "uninstall", "--yes"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("removed", result.Stdout);
        Assert.False(Directory.Exists(home.Path));
    }

    [Fact]
    public void SelfUninstall_WithoutYes_RefusesAndRemovesNothing()
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");

        var result = ZsupProcess.Run(home.Path, ["self", "uninstall"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--yes", result.Stderr);
        Assert.True(Directory.Exists(home.Path));
    }

    [Fact]
    public void SelfUninstall_AZsupInsideTheHome_IsRefusedOnWindows()
    {
        // The premise holds here: an executing file cannot be deleted on Windows, so removing the
        // home would half-delete it.
        if (!OperatingSystem.IsWindows())
            return;

        using var home = new TempHome();
        var installed = CopyZsupInto(home.Dir("bin"));

        var result = ZsupProcess.Run(
            home.Path,
            ["self", "uninstall", "--yes"],
            executable: installed
        );

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("cannot remove itself", result.Stderr);
        Assert.True(Directory.Exists(home.Path));
    }

    /// <summary>Copies the built zsup and everything it needs to run into <paramref name="dir" />.</summary>
    private static string CopyZsupInto(string dir)
    {
        var source = Path.GetDirectoryName(ZsupProcess.Executable)!;

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);

        return Path.Combine(dir, Path.GetFileName(ZsupProcess.Executable));
    }
}
