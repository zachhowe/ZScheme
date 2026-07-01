using System.IO.Compression;
using System.Text;
using Xunit;
using ZScheme.Compiler.Package.NuGet;

namespace ZScheme.Compiler.Tests.Package;

public class NupkgExtractorTests : IDisposable
{
    private readonly string _tempDir;

    public NupkgExtractorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"zscheme-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void ExtractsDlls_FromBestTfmFolder()
    {
        var nupkgPath = CreateTestNupkg(
            ("lib/net10.0/MyLib.dll", "net10-content"),
            ("lib/net8.0/MyLib.dll", "net8-content"),
            ("lib/netstandard2.0/MyLib.dll", "ns20-content")
        );

        var outputDir = Path.Combine(_tempDir, "output");
        Directory.CreateDirectory(outputDir);

        var dlls = NupkgExtractor.ExtractDlls(nupkgPath, outputDir);

        Assert.Single(dlls);
        Assert.Contains("MyLib.dll", dlls[0]);
        Assert.Equal("net10-content", File.ReadAllText(dlls[0]));
    }

    [Fact]
    public void FallsBackToNetStandard_WhenNoNetCoreAvailable()
    {
        var nupkgPath = CreateTestNupkg(
            ("lib/netstandard2.0/MyLib.dll", "ns20-content"),
            ("lib/net45/MyLib.dll", "net45-content")
        );

        var outputDir = Path.Combine(_tempDir, "output");
        Directory.CreateDirectory(outputDir);

        var dlls = NupkgExtractor.ExtractDlls(nupkgPath, outputDir);

        Assert.Single(dlls);
        Assert.Equal("ns20-content", File.ReadAllText(dlls[0]));
    }

    [Fact]
    public void ReturnsEmpty_WhenNoCompatibleTfm()
    {
        var nupkgPath = CreateTestNupkg(("lib/net45/MyLib.dll", "content"));

        var outputDir = Path.Combine(_tempDir, "output");
        Directory.CreateDirectory(outputDir);

        var dlls = NupkgExtractor.ExtractDlls(nupkgPath, outputDir);

        Assert.Empty(dlls);
    }

    [Fact]
    public void ReadNuspec_ParsesDependencies()
    {
        var nuspecXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>TestPackage</id>
                <version>1.2.3</version>
                <dependencies>
                  <group targetFramework=".NETCoreApp,Version=v8.0">
                    <dependency id="SomeDep" version="[2.0.0, )" />
                  </group>
                  <group targetFramework=".NETStandard,Version=v2.0">
                    <dependency id="OtherDep" version="1.0.0" />
                  </group>
                </dependencies>
              </metadata>
            </package>
            """;

        var nupkgPath = CreateTestNupkg(
            ("TestPackage.nuspec", nuspecXml),
            ("lib/net8.0/TestPackage.dll", "dll-content")
        );

        var info = NupkgExtractor.ReadNuspec(nupkgPath);

        Assert.Equal("TestPackage", info.Id);
        Assert.Equal("1.2.3", info.Version);
        Assert.Equal(2, info.DependencyGroups.Count);

        var net8Group = info.DependencyGroups.First(g => g.TargetFramework == "net8.0");
        Assert.Single(net8Group.Dependencies);
        Assert.Equal("SomeDep", net8Group.Dependencies[0].Id);
        Assert.Equal("[2.0.0, )", net8Group.Dependencies[0].VersionRange);
    }

    [Fact]
    public void ReadNuspec_HandlesFlatDependencies()
    {
        var nuspecXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>FlatPkg</id>
                <version>1.0.0</version>
                <dependencies>
                  <dependency id="Dep1" version="1.0.0" />
                  <dependency id="Dep2" version="2.0.0" />
                </dependencies>
              </metadata>
            </package>
            """;

        var nupkgPath = CreateTestNupkg(("FlatPkg.nuspec", nuspecXml));

        var info = NupkgExtractor.ReadNuspec(nupkgPath);

        Assert.Single(info.DependencyGroups);
        Assert.Null(info.DependencyGroups[0].TargetFramework);
        Assert.Equal(2, info.DependencyGroups[0].Dependencies.Count);
    }

    private string CreateTestNupkg(params (string entryName, string content)[] entries)
    {
        var path = Path.Combine(_tempDir, $"test-{Guid.NewGuid():N}.nupkg");
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entryName, content) in entries)
        {
            var entry = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return path;
    }
}
