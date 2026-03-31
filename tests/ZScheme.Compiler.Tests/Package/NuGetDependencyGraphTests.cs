using System.IO.Compression;
using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Package.NuGet;

namespace ZScheme.Compiler.Tests.Package;

public class NuGetDependencyGraphTests : IDisposable
{
    private readonly string _cacheRoot;
    private readonly MockNuGetV3Client _client = new();
    private readonly DiagnosticBag _diagnostics = new();

    public NuGetDependencyGraphTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), "zscheme-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_cacheRoot);

        // By default, OnDownload writes a minimal nupkg with no dependencies
        _client.OnDownload = (id, version, path) =>
            WriteNupkg(path, id, version, []);
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, true);
    }

    [Fact]
    public async Task ReturnsEmpty_WhenNoRoots()
    {
        var graph = CreateGraph();

        var result = await graph.ResolveAsync([]);

        Assert.Empty(result);
        Assert.Empty(_client.DownloadCalls);
    }

    [Fact]
    public async Task ResolvesSinglePackage()
    {
        var graph = CreateGraph();
        var roots = new List<NuGetDependency>
        {
            new("TestPackage", "1.0.0", SourceSpan.None)
        };

        var result = await graph.ResolveAsync(roots);

        Assert.Single(result);
        Assert.Equal("TestPackage", result[0].Id);
        Assert.Equal("1.0.0", result[0].Version);
        Assert.False(_diagnostics.HasErrors);

        var call = Assert.Single(_client.DownloadCalls);
        Assert.Equal("TestPackage", call.PackageId);
        Assert.Equal("1.0.0", call.Version);
    }

    [Fact]
    public async Task ResolvesTransitiveDependencies()
    {
        _client.OnDownload = (id, version, path) =>
        {
            if (id == "RootPackage")
                WriteNupkg(path, id, version,
                [
                    new NuspecDependencyRef("TransitiveA", "2.0.0")
                ]);
            else
                WriteNupkg(path, id, version, []);
        };

        var graph = CreateGraph();
        var roots = new List<NuGetDependency>
        {
            new("RootPackage", "1.0.0", SourceSpan.None)
        };

        var result = await graph.ResolveAsync(roots);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == "RootPackage" && r.Version == "1.0.0");
        Assert.Contains(result, r => r.Id == "TransitiveA" && r.Version == "2.0.0");
        Assert.False(_diagnostics.HasErrors);
    }

    [Fact]
    public async Task SkipsDuplicatePackages()
    {
        _client.OnDownload = (id, version, path) =>
        {
            if (id is "PackageA" or "PackageB")
                WriteNupkg(path, id, version,
                [
                    new NuspecDependencyRef("SharedDep", "1.0.0")
                ]);
            else
                WriteNupkg(path, id, version, []);
        };

        var graph = CreateGraph();
        var roots = new List<NuGetDependency>
        {
            new("PackageA", "1.0.0", SourceSpan.None),
            new("PackageB", "2.0.0", SourceSpan.None)
        };

        var result = await graph.ResolveAsync(roots);

        Assert.Equal(3, result.Count);
        // SharedDep should only be downloaded once
        Assert.Single(_client.DownloadCalls, c => c.PackageId == "SharedDep");
    }

    [Fact]
    public async Task ReportsError_WhenDownloadFails()
    {
        _client.OnDownload = (_, _, _) =>
            throw new HttpRequestException("Network error");

        var graph = CreateGraph();
        var roots = new List<NuGetDependency>
        {
            new("BadPackage", "1.0.0", SourceSpan.None)
        };

        var result = await graph.ResolveAsync(roots);

        Assert.Empty(result);
        Assert.True(_diagnostics.HasErrors);
    }

    [Fact]
    public async Task ResolvesVersionRange_UsingGetVersions()
    {
        _client.Versions["TransitiveDep"] = ["1.0.0", "1.1.0", "2.0.0"];

        _client.OnDownload = (id, version, path) =>
        {
            if (id == "RootPkg")
                WriteNupkg(path, id, version,
                [
                    new NuspecDependencyRef("TransitiveDep", "[1.0.0, 2.0.0)")
                ]);
            else
                WriteNupkg(path, id, version, []);
        };

        var graph = CreateGraph();
        var roots = new List<NuGetDependency>
        {
            new("RootPkg", "1.0.0", SourceSpan.None)
        };

        var result = await graph.ResolveAsync(roots);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == "TransitiveDep" && r.Version == "1.1.0");
        Assert.Contains(_client.GetVersionsCalls, c => c == "TransitiveDep");
        Assert.False(_diagnostics.HasErrors);
    }

    [Fact]
    public async Task ReportsError_WhenNoVersionSatisfiesRange()
    {
        _client.Versions["TransitiveDep"] = ["3.0.0", "4.0.0"];

        _client.OnDownload = (id, version, path) =>
        {
            if (id == "RootPkg")
                WriteNupkg(path, id, version,
                [
                    new NuspecDependencyRef("TransitiveDep", "[1.0.0, 2.0.0)")
                ]);
            else
                WriteNupkg(path, id, version, []);
        };

        var graph = CreateGraph();
        var roots = new List<NuGetDependency>
        {
            new("RootPkg", "1.0.0", SourceSpan.None)
        };

        var result = await graph.ResolveAsync(roots);

        // RootPkg resolves but TransitiveDep does not
        Assert.Single(result);
        Assert.True(_diagnostics.HasErrors);
    }

    [Fact]
    public async Task UsesExactVersion_WhenNoBracketsOrCommas()
    {
        _client.OnDownload = (id, version, path) =>
        {
            if (id == "Root")
                WriteNupkg(path, id, version,
                [
                    new NuspecDependencyRef("Child", "3.5.0")
                ]);
            else
                WriteNupkg(path, id, version, []);
        };

        var graph = CreateGraph();
        var roots = new List<NuGetDependency>
        {
            new("Root", "1.0.0", SourceSpan.None)
        };

        var result = await graph.ResolveAsync(roots);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == "Child" && r.Version == "3.5.0");
        // Should NOT call GetVersionsAsync for exact versions
        Assert.DoesNotContain("Child", _client.GetVersionsCalls);
    }

    private NuGetDependencyGraph CreateGraph()
    {
        return new NuGetDependencyGraph(_client, _cacheRoot, _diagnostics);
    }

    private static void WriteNupkg(
        string path,
        string id,
        string version,
        IReadOnlyList<NuspecDependencyRef> dependencies)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        var nuspecEntry = zip.CreateEntry($"{id}.nuspec");
        using var writer = new StreamWriter(nuspecEntry.Open());

        writer.Write($"""
                      <?xml version="1.0" encoding="utf-8"?>
                      <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                        <metadata>
                          <id>{id}</id>
                          <version>{version}</version>
                          <dependencies>
                      """);

        if (dependencies.Count > 0)
        {
            writer.Write("""
                                   <group targetFramework="net10.0">
                         """);
            foreach (var dep in dependencies)
                writer.Write($"""
                                            <dependency id="{dep.Id}" version="{dep.VersionRange}" />
                              """);
            writer.Write("""
                                   </group>
                         """);
        }

        writer.Write("""
                         </dependencies>
                       </metadata>
                     </package>
                     """);
    }
}
