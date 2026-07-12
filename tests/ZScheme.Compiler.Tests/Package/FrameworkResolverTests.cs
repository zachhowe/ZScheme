using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

/// <summary>
///     Tests <see cref="FrameworkResolver" /> against a fake dotnet root pointed to by
///     DOTNET_ROOT. Every test that mutates the (process-wide) env var lives in this one class,
///     restoring the previous value on dispose; xUnit runs tests within a class serially, so
///     the mutation never races another test.
/// </summary>
public class FrameworkResolverTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"zs_fwres_test_{Guid.NewGuid():N}"
    );

    private readonly string? _savedDotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");

    public FrameworkResolverTests()
    {
        Directory.CreateDirectory(_tempRoot);
        Environment.SetEnvironmentVariable("DOTNET_ROOT", _tempRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DOTNET_ROOT", _savedDotnetRoot);
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
        Assert.Empty(FrameworkResolver.Resolve([], diag));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void PicksHighestParseableVersion()
    {
        AddFrameworkVersions("Microsoft.AspNetCore.App", "9.0.1", "10.0.0", "not-a-version");
        var diag = new DiagnosticBag();

        var result = FrameworkResolver.Resolve([Fw("Microsoft.AspNetCore.App")], diag);

        Assert.False(diag.HasErrors);
        var path = Assert.Single(result);
        Assert.Equal(
            Path.Combine(_tempRoot, "shared", "Microsoft.AspNetCore.App", "10.0.0"),
            path
        );
    }

    [Fact]
    public void MissingFrameworkReportsNotInstalled()
    {
        var diag = new DiagnosticBag();

        var result = FrameworkResolver.Resolve([Fw("Missing.Framework")], diag);

        Assert.Empty(result);
        var d = Assert.Single(diag.Diagnostics);
        Assert.Contains("is not installed at", d.Message);
    }

    [Fact]
    public void FrameworkWithOnlyUnparseableVersionsReportsNoVersions()
    {
        AddFrameworkVersions("Weird.Framework", "not-a-version", "also-bad");
        var diag = new DiagnosticBag();

        var result = FrameworkResolver.Resolve([Fw("Weird.Framework")], diag);

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

        var result = FrameworkResolver.Resolve([Fw("Fw.One"), Fw("Fw.Two")], diag);

        Assert.Equal(2, result.Count);
        Assert.EndsWith(Path.Combine("Fw.One", "8.0.0"), result[0]);
        Assert.EndsWith(Path.Combine("Fw.Two", "8.0.4"), result[1]);
    }

    [Fact]
    public void MissingFrameworkDoesNotBlockResolvableOnes()
    {
        AddFrameworkVersions("Fw.Present", "9.0.0");
        var diag = new DiagnosticBag();

        var result = FrameworkResolver.Resolve([Fw("Fw.Absent"), Fw("Fw.Present")], diag);

        Assert.Single(result);
        Assert.True(diag.HasErrors);
    }
}
