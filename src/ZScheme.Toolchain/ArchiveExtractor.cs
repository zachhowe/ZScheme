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
    /// <remarks>
    ///     The whole source is listed before the destination is created, and the walk does not
    ///     descend into reparse points. Both matter for termination rather than for tidiness.
    ///     <see cref="SearchOption.AllDirectories" /> enumerates lazily, so a destination *inside*
    ///     the source — <c>zsup install dev --from ~/.zscheme</c>, whose staging slot is under
    ///     <c>downloads/</c> — has every directory this loop creates handed back to the same
    ///     enumerator as one more level to descend into, and the copy writes until the disk fills.
    ///     A junction or symlink pointing back at an ancestor does the same with no overlap needed,
    ///     because the <c>SearchOption</c> overloads skip no attributes at all.
    /// </remarks>
    public static void CopyDirectory(string sourceDir, string destDir)
    {
        var dirs = new List<string>();
        var files = new List<string>();
        Collect(sourceDir, dirs, files);

        Directory.CreateDirectory(destDir);

        foreach (var dir in dirs)
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, dir)));

        foreach (var file in files)
        {
            var target = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    ///     Lists every directory and file under <paramref name="root" />, without following
    ///     directory symlinks or junctions.
    /// </summary>
    private static void Collect(string root, List<string> dirs, List<string> files)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            files.AddRange(Directory.GetFiles(current));

            foreach (var sub in Directory.GetDirectories(current))
            {
                dirs.Add(sub);

                // Recreated as an empty directory rather than descended into: a reparse point can
                // name an ancestor of itself, and following one has no fixed point.
                if (!new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    pending.Push(sub);
            }
        }
    }
}
