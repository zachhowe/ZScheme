using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class ToolchainResolverTests
{
    private static ToolchainResolver ResolverFor(TempHome home)
    {
        return new ToolchainResolver(new ToolchainRegistry(home.Path));
    }

    [Fact]
    public void Resolve_NothingSelected_ReportsNoToolchains()
    {
        using var home = new TempHome();

        var result = ResolverFor(home).Resolve(envVersion: null, home.Dir("proj"));

        Assert.IsType<ToolchainResolution.NoToolchains>(result);
    }

    [Fact]
    public void Resolve_GlobalDefault_IsTheFallback()
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");
        new ToolchainRegistry(home.Path).SetDefault("0.4.0");

        var result = ResolverFor(home).Resolve(envVersion: null, home.Dir("proj"));

        var resolved = Assert.IsType<ToolchainResolution.Resolved>(result);
        Assert.Equal("0.4.0", resolved.Toolchain.Name);
        Assert.Equal(ToolchainOrigin.GlobalDefault, resolved.Origin);
    }

    [Fact]
    public void Resolve_ProjectFile_BeatsTheGlobalDefault()
    {
        using var home = new TempHome();
        home.AddInstalled("0.3.0");
        home.AddInstalled("0.4.0");
        new ToolchainRegistry(home.Path).SetDefault("0.4.0");

        var proj = home.Dir("proj");
        var pin = Path.Combine(proj, ZSchemeHome.VersionFileName);
        File.WriteAllText(pin, "0.3.0");

        var result = ResolverFor(home).Resolve(envVersion: null, proj);

        var resolved = Assert.IsType<ToolchainResolution.Resolved>(result);
        Assert.Equal("0.3.0", resolved.Toolchain.Name);
        Assert.Equal(ToolchainOrigin.ProjectFile, resolved.Origin);
        Assert.Equal(pin, resolved.OriginDetail);
    }

    [Fact]
    public void Resolve_EnvironmentVariable_BeatsEverything()
    {
        using var home = new TempHome();
        home.AddInstalled("0.3.0");
        home.AddInstalled("0.4.0");
        new ToolchainRegistry(home.Path).SetDefault("0.3.0");

        var proj = home.Dir("proj");
        File.WriteAllText(Path.Combine(proj, ZSchemeHome.VersionFileName), "0.3.0");

        var result = ResolverFor(home).Resolve("0.4.0", proj);

        var resolved = Assert.IsType<ToolchainResolution.Resolved>(result);
        Assert.Equal("0.4.0", resolved.Toolchain.Name);
        Assert.Equal(ToolchainOrigin.EnvironmentVariable, resolved.Origin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankEnvironmentVariable_IsIgnored(string blank)
    {
        using var home = new TempHome();
        home.AddInstalled("0.4.0");
        new ToolchainRegistry(home.Path).SetDefault("0.4.0");

        var result = ResolverFor(home).Resolve(blank, home.Dir("proj"));

        var resolved = Assert.IsType<ToolchainResolution.Resolved>(result);
        Assert.Equal(ToolchainOrigin.GlobalDefault, resolved.Origin);
    }

    [Fact]
    public void Resolve_PinnedButNotInstalled_ReportsTheOriginatingFile()
    {
        using var home = new TempHome();
        var proj = home.Dir("proj");
        var pin = Path.Combine(proj, ZSchemeHome.VersionFileName);
        File.WriteAllText(pin, "0.9.9");

        var result = ResolverFor(home).Resolve(envVersion: null, proj);

        var missing = Assert.IsType<ToolchainResolution.NotInstalled>(result);
        Assert.Equal("0.9.9", missing.Name);
        Assert.Equal(ToolchainOrigin.ProjectFile, missing.Origin);
        Assert.Equal(pin, missing.OriginDetail);
    }

    [Fact]
    public void Resolve_EnvSelectsAMissingToolchain_ReportsTheEnvironmentOrigin()
    {
        using var home = new TempHome();

        var result = ResolverFor(home).Resolve("0.9.9", home.Dir("proj"));

        var missing = Assert.IsType<ToolchainResolution.NotInstalled>(result);
        Assert.Equal(ToolchainOrigin.EnvironmentVariable, missing.Origin);
    }

    [Fact]
    public void Resolve_DefaultPointsAtAnUninstalledToolchain_ReportsGlobalDefault()
    {
        using var home = new TempHome();
        // Written directly: SetDefault/Remove keep the two in sync, and this is the inconsistent
        // state left behind when a toolchain directory is deleted outside zsup.
        File.WriteAllText(
            ZSchemeHome.GetSettingsFile(home.Path),
            """{"formatVersion":1,"defaultToolchain":"0.4.0"}"""
        );

        var result = ResolverFor(home).Resolve(envVersion: null, home.Dir("proj"));

        var missing = Assert.IsType<ToolchainResolution.NotInstalled>(result);
        Assert.Equal(ToolchainOrigin.GlobalDefault, missing.Origin);
    }

    [Fact]
    public void Resolve_BrokenLink_IsReportedDistinctly()
    {
        using var home = new TempHome();
        var target = Path.Combine(home.Path, "gone");
        home.AddLink("dev", target);
        new ToolchainRegistry(home.Path).SetDefault("dev");

        var result = ResolverFor(home).Resolve(envVersion: null, home.Dir("proj"));

        var broken = Assert.IsType<ToolchainResolution.LinkBroken>(result);
        Assert.Equal("dev", broken.Name);
        Assert.Equal(Path.GetFullPath(target), broken.TargetPath);
    }

    [Fact]
    public void Resolve_TraversalInPinFile_IsTreatedAsNotInstalled()
    {
        using var home = new TempHome();
        var proj = home.Dir("proj");
        File.WriteAllText(Path.Combine(proj, ZSchemeHome.VersionFileName), "../../../etc");

        var result = ResolverFor(home).Resolve(envVersion: null, proj);

        Assert.IsType<ToolchainResolution.NotInstalled>(result);
    }
}
