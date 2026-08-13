using System.Formats.Tar;
using System.IO.Compression;
using Xunit;

namespace ZScheme.Toolchain.Tests;

public sealed class ArchiveExtractorTests
{
    [Theory]
    [InlineData("a.zip", true)]
    [InlineData("a.tar.gz", true)]
    [InlineData("a.tgz", true)]
    [InlineData("A.ZIP", true)]
    [InlineData("a.7z", false)]
    [InlineData("somedir", false)]
    public void IsArchive_RecognizesTheShippedFormats(string name, bool expected)
    {
        Assert.Equal(expected, ArchiveExtractor.IsArchive(name));
    }

    [Fact]
    public void Extract_Zip_WritesTheEntries()
    {
        using var home = new TempHome();
        var zip = Path.Combine(home.Path, "t.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "bin/zs", "binary");
            WriteEntry(archive, "packages/stdlib/package.zspkg", "manifest");
        }

        var dest = Path.Combine(home.Path, "out");
        ArchiveExtractor.Extract(zip, dest);

        Assert.Equal("binary", File.ReadAllText(Path.Combine(dest, "bin", "zs")));
        Assert.Equal(
            "manifest",
            File.ReadAllText(Path.Combine(dest, "packages", "stdlib", "package.zspkg"))
        );
    }

    [Fact]
    public void Extract_TarGz_WritesTheEntries()
    {
        using var home = new TempHome();
        var payload = Path.Combine(home.Path, "payload");
        Directory.CreateDirectory(Path.Combine(payload, "bin"));
        File.WriteAllText(Path.Combine(payload, "bin", "zs"), "binary");

        var tarGz = Path.Combine(home.Path, "t.tar.gz");
        CreateTarGz(payload, tarGz);

        var dest = Path.Combine(home.Path, "out");
        ArchiveExtractor.Extract(tarGz, dest);

        Assert.Equal("binary", File.ReadAllText(Path.Combine(dest, "bin", "zs")));
    }

    [Fact]
    public void Extract_TarGz_PreservesTheExecutableBitOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return; // Unix file modes do not exist here.

        using var home = new TempHome();
        var payload = Path.Combine(home.Path, "payload");
        Directory.CreateDirectory(payload);
        var exe = Path.Combine(payload, "zs");
        File.WriteAllText(exe, "binary");
        File.SetUnixFileMode(exe, ShimInstaller.Executable755);

        var tarGz = Path.Combine(home.Path, "t.tar.gz");
        CreateTarGz(payload, tarGz);

        var dest = Path.Combine(home.Path, "out");
        ArchiveExtractor.Extract(tarGz, dest);

        var mode = File.GetUnixFileMode(Path.Combine(dest, "zs"));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void Extract_Zip_RefusesToEscapeTheDestination()
    {
        using var home = new TempHome();
        var zip = Path.Combine(home.Path, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            WriteEntry(archive, "../escaped.txt", "pwned");

        var dest = Path.Combine(home.Path, "out");

        // The BCL extractor already enforces this; the test pins the guarantee we rely on.
        Assert.ThrowsAny<IOException>(() => ArchiveExtractor.Extract(zip, dest));
        Assert.False(File.Exists(Path.Combine(home.Path, "escaped.txt")));
    }

    [Fact]
    public void Extract_UnknownFormat_Throws()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "t.7z");
        File.WriteAllText(path, "nope");

        Assert.Throws<NotSupportedException>(() =>
            ArchiveExtractor.Extract(path, Path.Combine(home.Path, "out"))
        );
    }

    [Fact]
    public void CopyDirectory_CopiesNestedContent()
    {
        using var home = new TempHome();
        var source = Path.Combine(home.Path, "src");
        Directory.CreateDirectory(Path.Combine(source, "a", "b"));
        File.WriteAllText(Path.Combine(source, "root.txt"), "root");
        File.WriteAllText(Path.Combine(source, "a", "b", "deep.txt"), "deep");
        Directory.CreateDirectory(Path.Combine(source, "empty"));

        var dest = Path.Combine(home.Path, "dst");
        ArchiveExtractor.CopyDirectory(source, dest);

        Assert.Equal("root", File.ReadAllText(Path.Combine(dest, "root.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(dest, "a", "b", "deep.txt")));
        Assert.True(Directory.Exists(Path.Combine(dest, "empty")));
    }

    [Fact]
    public void CopyDirectory_IntoASubdirectoryOfItself_Terminates()
    {
        using var home = new TempHome();
        var source = Path.Combine(home.Path, "src");
        Directory.CreateDirectory(Path.Combine(source, "a"));
        File.WriteAllText(Path.Combine(source, "a", "deep.txt"), "deep");

        // The destination is inside the source, which is what `zsup install --from ~/.zscheme`
        // produces: staging lives under downloads/, under the home being copied.
        var dest = Path.Combine(source, "nested", "dst");
        ArchiveExtractor.CopyDirectory(source, dest);

        Assert.Equal("deep", File.ReadAllText(Path.Combine(dest, "a", "deep.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "nested", "dst", "nested")));
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static void CreateTarGz(string sourceDir, string destPath)
    {
        using var file = File.Create(destPath);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        TarFile.CreateFromDirectory(sourceDir, gzip, includeBaseDirectory: false);
    }
}
