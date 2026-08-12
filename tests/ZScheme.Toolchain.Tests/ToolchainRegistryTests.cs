using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class ToolchainRegistryTests
{
    [Fact]
    public void List_EmptyHome_ReturnsNothing()
    {
        using var home = new TempHome();

        Assert.Empty(new ToolchainRegistry(home.Path).List());
    }

    [Fact]
    public void List_ReturnsInstalledAndLinked_SortedByName()
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");
        home.AddInstalled("0.3.0");
        home.AddLink("dev", home.Dir("devtree"));

        var names = new ToolchainRegistry(home.Path).List().Select(t => t.Name).ToArray();

        Assert.Equal(["0.3.0", "0.4.0", "dev"], names);
    }

    [Fact]
    public void UsingCompilerVersion_FindsEveryToolchainBuiltFromTheSamePayload()
    {
        // What makes `zsup uninstall --purge-cache` safe: cache/pkg/<version> is keyed by compiler
        // version, so removing one of these toolchains must not delete the cache the others read.
        using var home = new TempHome();
        home.AddInstalled("0.4.0", compilerVersion: "0.4.0");
        home.AddInstalled("dev-copy", compilerVersion: "0.4.0");
        home.AddInstalled("0.3.0", compilerVersion: "0.3.0");

        var names = new ToolchainRegistry(home.Path)
            .UsingCompilerVersion("0.4.0")
            .Select(t => t.Name)
            .ToArray();

        Assert.Equal(["0.4.0", "dev-copy"], names);
    }

    [Fact]
    public void UsingCompilerVersion_FallsBackToTheInstallNameWithoutMetadata()
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");

        Assert.Equal(
            ["0.4.0"],
            new ToolchainRegistry(home.Path).UsingCompilerVersion("0.4.0").Select(t => t.Name)
        );
    }

    [Fact]
    public void UsingCompilerVersion_IgnoresLinkedToolchains()
    {
        // A linked toolchain reports the released version but reads its own cache-dev/<name>/, so
        // it is not a reason to keep the shared one.
        using var home = new TempHome();
        home.AddLink("dev", home.Dir("devtree"));

        Assert.Empty(new ToolchainRegistry(home.Path).UsingCompilerVersion("dev"));
    }

    [Fact]
    public void List_IgnoresTransientStagingDirectories()
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");
        Directory.CreateDirectory(
            Path.Combine(ZSchemeHome.GetToolchainsDir(home.Path), ".staging-abc123")
        );

        var names = new ToolchainRegistry(home.Path).List().Select(t => t.Name).ToArray();

        Assert.Equal(["0.4.0"], names);
    }

    [Fact]
    public void List_ADirectoryAndALinkOfTheSameName_ReportsItOnce()
    {
        // The installer refuses to create this collision, but a home predating that guard -- or one
        // edited by hand -- can still have both. Listing the name twice would suggest `zsup
        // uninstall` could leave one behind, which is exactly what used to happen.
        using var home = new TempHome();
        home.AddInstalled("dev");
        home.AddLink("dev", home.Dir("devtree"));

        var listed = new ToolchainRegistry(home.Path).List();

        var toolchain = Assert.Single(listed);
        Assert.Equal("dev", toolchain.Name);
        // The directory wins, matching TryGet.
        Assert.False(toolchain.IsLinked);
    }

    [Fact]
    public void TryGet_InstalledToolchain_PointsAtItsBinDir()
    {
        using var home = new TempHome();
        var binDir = home.AddInstalled("0.4.0");

        var toolchain = new ToolchainRegistry(home.Path).TryGet("0.4.0");

        Assert.NotNull(toolchain);
        Assert.False(toolchain.IsLinked);
        Assert.Equal(binDir, toolchain.BinDir);
        Assert.Equal(
            Path.Combine(binDir, ZSchemeHome.ExeName("zs")),
            toolchain.GetExecutablePath("zs")
        );
    }

    [Fact]
    public void TryGet_LinkedDevTree_UsesTheTargetDirectlyWhenBinariesSitAtItsRoot()
    {
        using var home = new TempHome();
        var target = home.Dir("build-output");
        File.WriteAllText(Path.Combine(target, ZSchemeHome.ExeName("zs")), "stub");
        home.AddLink("dev", target);

        var toolchain = new ToolchainRegistry(home.Path).TryGet("dev");

        Assert.NotNull(toolchain);
        Assert.True(toolchain.IsLinked);
        Assert.Equal(target, toolchain.BinDir);
        Assert.Equal(target, toolchain.LinkTargetPath);
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsNull()
    {
        using var home = new TempHome();

        Assert.Null(new ToolchainRegistry(home.Path).TryGet("nope"));
    }

    [Fact]
    public void TryGet_UnsafeName_ReturnsNullRatherThanThrowing()
    {
        using var home = new TempHome();

        // A hostile .zscheme-version must not be able to reach outside toolchains/.
        Assert.Null(new ToolchainRegistry(home.Path).TryGet("../../etc"));
    }

    [Fact]
    public void IsLinkBroken_DetectsAMissingTarget()
    {
        using var home = new TempHome();
        home.AddLink("dev", Path.Combine(home.Path, "does-not-exist"));

        var toolchain = new ToolchainRegistry(home.Path).TryGet("dev");

        Assert.NotNull(toolchain);
        Assert.True(ToolchainRegistry.IsLinkBroken(toolchain));
    }

    [Fact]
    public void Default_RoundTrips()
    {
        using var home = new TempHome();
        var registry = new ToolchainRegistry(home.Path);
        home.AddInstalled("0.4.0");

        Assert.Null(registry.GetDefault());

        registry.SetDefault("0.4.0");
        Assert.Equal("0.4.0", new ToolchainRegistry(home.Path).GetDefault());

        registry.ClearDefault();
        Assert.Null(new ToolchainRegistry(home.Path).GetDefault());
    }

    [Fact]
    public void GetDefault_MalformedSettings_DegradesToNoDefault()
    {
        using var home = new TempHome();
        File.WriteAllText(ZSchemeHome.GetSettingsFile(home.Path), "{ not json");

        Assert.Null(new ToolchainRegistry(home.Path).GetDefault());
    }

    [Fact]
    public void Link_RejectsAMissingTarget()
    {
        using var home = new TempHome();
        var registry = new ToolchainRegistry(home.Path);

        Assert.Throws<DirectoryNotFoundException>(() =>
            registry.Link("dev", Path.Combine(home.Path, "nope"))
        );
    }

    [Fact]
    public void Link_RejectsANameAlreadyInstalled()
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");
        var registry = new ToolchainRegistry(home.Path);

        Assert.Throws<IOException>(() => registry.Link("0.4.0", home.Dir("devtree")));
    }

    [Fact]
    public void Remove_DeletesTheToolchainAndClearsTheDefault()
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");
        var registry = new ToolchainRegistry(home.Path);
        registry.SetDefault("0.4.0");

        registry.Remove("0.4.0");

        Assert.Null(registry.TryGet("0.4.0"));
        Assert.Null(registry.GetDefault());
    }

    [Fact]
    public void Remove_LeavesADifferentDefaultAlone()
    {
        using var home = new TempHome();
        home.AddInstalled("0.3.0");
        home.AddInstalled("0.4.0");
        var registry = new ToolchainRegistry(home.Path);
        registry.SetDefault("0.4.0");

        registry.Remove("0.3.0");

        Assert.Equal("0.4.0", registry.GetDefault());
    }

    [Fact]
    public void Unlink_RemovesTheLinkButNotTheTarget()
    {
        using var home = new TempHome();
        var target = home.Dir("devtree");
        var registry = new ToolchainRegistry(home.Path);
        registry.Link("dev", target);

        registry.Unlink("dev");

        Assert.Null(registry.TryGet("dev"));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void TryGet_MalformedLinkFile_BehavesAsAbsent()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(ZSchemeHome.GetToolchainsDir(home.Path));
        // A path the platform cannot parse: this must not throw out of every zs invocation.
        File.WriteAllText(ZSchemeHome.GetToolchainLinkFile("dev", home.Path), "\0not|a*path\n");

        Assert.Null(new ToolchainRegistry(home.Path).TryGet("dev"));
    }

    [Fact]
    public void Remove_ClearsTheDefaultEvenWhenTheNameDiffersInCase()
    {
        // On Windows and default macOS the directory resolves case-insensitively, so removal
        // succeeds; the recorded default has to be cleared with the same comparison or every
        // later `zs` fails with "the default toolchain is not installed".
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return;

        using var home = new TempHome();
        home.AddInstalled("Dev");
        var registry = new ToolchainRegistry(home.Path);
        registry.SetDefault("Dev");

        registry.Remove("dev");

        Assert.Null(registry.GetDefault());
    }

    [Fact]
    public void Unlink_UnknownName_Throws()
    {
        using var home = new TempHome();

        Assert.Throws<FileNotFoundException>(() => new ToolchainRegistry(home.Path).Unlink("dev"));
    }
}
