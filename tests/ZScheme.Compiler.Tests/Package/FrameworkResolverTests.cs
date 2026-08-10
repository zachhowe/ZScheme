using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

/// <summary>
///     Tests <see cref="FrameworkResolver" /> against a fake dotnet root, passed in explicitly.
///     <para>
///         These used to point the resolver at that root by setting <c>DOTNET_ROOT</c>. Serializing
///         within the class is not enough: the env var is process-wide and xUnit runs
///         <em>other</em> classes in parallel, so any test resolving a real framework in that
///         window saw the fake root instead — which is what made
///         <c>PackageAutoInstallerTests.FrameworkInheritedFromADependencyIsResolvedForAutoInstall</c>
///         fail intermittently. Nothing here mutates process state now.
///     </para>
/// </summary>
public class FrameworkResolverTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"zs_fwres_test_{Guid.NewGuid():N}"
    );

    public FrameworkResolverTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, true);
    }

    private void AddFrameworkVersions(string id, params string[] versions)
    {
        foreach (var v in versions)
            Directory.CreateDirectory(Path.Combine(_tempRoot, "shared", id, v));
    }

    private static FrameworkDependency Fw(string id) => new(id, SourceSpan.None);

    [Fact]
    public void EmptyDependencyListResolvesToEmpty()
    {
        var diag = new DiagnosticBag();
        Assert.Empty(FrameworkResolver.Resolve([], diag, _tempRoot));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void PicksHighestParseableVersion()
    {
        AddFrameworkVersions("Microsoft.AspNetCore.App", "9.0.1", "10.0.0", "not-a-version");
        var diag = new DiagnosticBag();

        var result = FrameworkResolver.Resolve([Fw("Microsoft.AspNetCore.App")], diag, _tempRoot);

        Assert.False(diag.HasErrors);
        var path = Assert.Single(result);
        Assert.Equal(Path.Combine(_tempRoot, "shared", "Microsoft.AspNetCore.App", "10.0.0"), path);
    }

    [Fact]
    public void MissingFrameworkReportsNotInstalled()
    {
        var diag = new DiagnosticBag();

        var result = FrameworkResolver.Resolve([Fw("Missing.Framework")], diag, _tempRoot);

        Assert.Empty(result);
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("is not installed at", d.Message);
    }

    [Fact]
    public void FrameworkWithOnlyUnparseableVersionsReportsNoVersions()
    {
        AddFrameworkVersions("Weird.Framework", "not-a-version", "also-bad");
        var diag = new DiagnosticBag();

        var result = FrameworkResolver.Resolve([Fw("Weird.Framework")], diag, _tempRoot);

        Assert.Empty(result);
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("No installed versions", d.Message);
    }

    [Fact]
    public void MultipleFrameworksResolveInOrder()
    {
        AddFrameworkVersions("Fw.One", "8.0.0");
        AddFrameworkVersions("Fw.Two", "8.0.4");
        var diag = new DiagnosticBag();

        var result = FrameworkResolver.Resolve([Fw("Fw.One"), Fw("Fw.Two")], diag, _tempRoot);

        Assert.Equal(2, result.Count);
        Assert.EndsWith(Path.Combine("Fw.One", "8.0.0"), result[0]);
        Assert.EndsWith(Path.Combine("Fw.Two", "8.0.4"), result[1]);
    }

    /// <summary>The root the production callers actually get. Asserted structurally rather than
    ///     against a fixed path so it holds whether or not DOTNET_ROOT is set in the environment —
    ///     setting it here is exactly what this class no longer does.</summary>
    [Fact]
    public void DefaultDotnetRootIsAnInstalledDotnetRoot()
    {
        var root = FrameworkResolver.DefaultDotnetRoot();

        Assert.False(string.IsNullOrEmpty(root));
        Assert.True(Directory.Exists(root), root);
        Assert.True(Directory.Exists(Path.Combine(root, "shared")), root);
    }

    [Fact]
    public void MissingFrameworkDoesNotBlockResolvableOnes()
    {
        AddFrameworkVersions("Fw.Present", "9.0.0");
        var diag = new DiagnosticBag();

        var result = FrameworkResolver.Resolve(
            [Fw("Fw.Absent"), Fw("Fw.Present")],
            diag,
            _tempRoot
        );

        Assert.Single(result);
        Assert.True(diag.HasErrors);
    }
}
