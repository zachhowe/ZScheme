using Xunit;
using ZScheme.Toolchain;
using ZScheme.Toolchain.Tests;

namespace ZScheme.Zsup.Tests;

public sealed class LinkCommandTests
{
    /// <summary>
    ///     The link is reported from the path the command resolved and wrote, not from a read-back
    ///     of the file. The read-back can legitimately answer null — a concurrent `zsup unlink` or a
    ///     --force install deletes the link file, and a scanner can hold the fresh file on Windows,
    ///     both of which ReadLinkTarget turns into "as if absent" by design — and asserting it with
    ///     `!` made those a bare NullReferenceException printed before the success line, for a link
    ///     that had in fact been created.
    /// </summary>
    [Fact]
    public void Link_ReportsTheDirectoryItWasGiven()
    {
        using var home = new TempHome();
        var target = home.Dir("build");
        File.WriteAllText(Path.Combine(target, ZSchemeHome.ExeName("zs")), "stub");
        File.WriteAllText(Path.Combine(target, ZSchemeHome.ExeName("zs-lsp")), "stub");

        var result = ZsupProcess.Run(home.Path, ["link", "dev", target]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"linked 'dev' -> {target}", result.Stdout);
        Assert.Equal("", result.Stderr);
        Assert.Equal(target, new ToolchainRegistry(home.Path).TryGet("dev")!.Dir);
    }

    [Fact]
    public void Link_ADirectoryWithoutTheBinaries_StillLinksAndWarns()
    {
        using var home = new TempHome();
        var target = home.Dir("build");

        var result = ZsupProcess.Run(home.Path, ["link", "dev", target]);

        // A warning rather than a refusal: the tree may simply not be built yet.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"linked 'dev' -> {target}", result.Stdout);
        Assert.Contains(ZSchemeHome.ExeName("zs"), result.Stderr);
        Assert.Contains(ZSchemeHome.ExeName("zs-lsp"), result.Stderr);
    }

    [Fact]
    public void Link_ADirectoryThatIsNotThere_IsRefused()
    {
        using var home = new TempHome();

        var result = ZsupProcess.Run(home.Path, ["link", "dev", Path.Combine(home.Path, "nope")]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("no such directory", result.Stderr);
        Assert.Equal("", result.Stdout);
    }
}
