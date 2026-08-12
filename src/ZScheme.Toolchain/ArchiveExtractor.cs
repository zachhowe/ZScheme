using System.Formats.Tar;
using System.IO.Compression;

namespace ZScheme.Toolchain;

/// <summary>Unpacks the two archive shapes releases ship in.</summary>
public static class ArchiveExtractor
{
    /// <summary>True when the path looks like an archive this class can open.</summary>
    public static bool IsArchive(string path)
    {
        return path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Extracts <paramref name="archivePath" /> into <paramref name="destDir" />.
    /// </summary>
    /// <remarks>
    ///     Both BCL extractors already refuse to write outside the destination, so traversal
    ///     protection is inherited rather than reimplemented here — the tests assert it holds.
    /// </remarks>
    public static void Extract(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
            return;
        }

        if (
            archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
        )
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            // TarFile applies each entry's Unix mode on Unix; callers still force 0755 on the
            // executables afterwards, because a tarball built on Windows records mode 0644.
            TarFile.ExtractToDirectory(gzip, destDir, overwriteFiles: true);
            return;
        }

        throw new NotSupportedException($"Unsupported archive format: {archivePath}");
    }

    /// <summary>Recursively copies a directory, used by <c>zsup install --from &lt;dir&gt;</c>.</summary>
    public static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (
            var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories)
        )
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
