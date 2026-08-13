namespace ZScheme.Toolchain;

/// <summary>
///     Finds the nearest <c>.zscheme-version</c> pin by walking up from a starting directory.
/// </summary>
public static class VersionFileLocator
{
    /// <param name="FilePath">Absolute path of the pin file that was found.</param>
    /// <param name="ToolchainName">The name it selects.</param>
    public sealed record Hit(string FilePath, string ToolchainName);

    /// <summary>
    ///     Walks up from <paramref name="startDir" /> to the filesystem root, returning the first
    ///     readable, non-empty pin file. Unlike the package auto-installer's bounded scan there is
    ///     no depth limit: a project can be nested arbitrarily deep and each level costs one
    ///     <c>File.Exists</c>.
    /// </summary>
    /// <param name="startDir">
    ///     Where to start walking, or <c>null</c> when the caller has no directory to offer. On Unix
    ///     a process keeps running with its working directory unlinked and <c>getcwd</c> then fails,
    ///     so "no directory to search" is a state callers genuinely reach — and it is
    ///     indistinguishable from "no pin found", which is what it answers.
    /// </param>
    public static Hit? Find(string? startDir)
    {
        if (string.IsNullOrEmpty(startDir))
            return null;

        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(Path.GetFullPath(startDir));
        }
        catch (Exception e)
            when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        for (; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ZSchemeHome.VersionFileName);
            if (!File.Exists(candidate))
                continue;

            var name = ReadToolchainName(candidate);
            if (name is not null)
                return new Hit(candidate, name);
        }

        return null;
    }

    /// <summary>
    ///     Reads the selected name: the first non-empty, non-comment line, put through
    ///     <see cref="ToolchainName.Sanitize" />. Anything after that first line is ignored, which
    ///     leaves room to grow the format later without breaking files written today. A line that
    ///     sanitizes away entirely reads as null, so <see cref="Find" /> treats it as the blank line
    ///     it effectively is and carries on to the next ancestor.
    /// </summary>
    public static string? ReadToolchainName(string versionFilePath)
    {
        try
        {
            foreach (var line in File.ReadLines(versionFilePath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                return ToolchainName.Sanitize(trimmed);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable pin file behaves as if it were absent rather than breaking every
            // command run from that directory. Note UnauthorizedAccessException is not an
            // IOException, and a checked-in pin file owned by another user is a real case.
        }

        return null;
    }
}
