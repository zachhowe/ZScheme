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
    public static Hit? Find(string startDir)
    {
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
    ///     Reads the selected name: the first non-empty, non-comment line, trimmed. Anything after
    ///     that first line is ignored, which leaves room to grow the format later without breaking
    ///     files written today.
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

                return Sanitize(trimmed);
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

    /// <summary>
    ///     Makes a pin's contents safe to echo. The value is attacker-controlled — it arrives in a
    ///     file checked into whatever repository the user cloned — and an invalid one is reported
    ///     back to the terminal, so control characters (ANSI/OSC escapes) are stripped and the
    ///     length is bounded.
    /// </summary>
    private static string Sanitize(string value)
    {
        const int maxLength = 64;

        var cleaned = new string([.. value.Where(c => !char.IsControl(c))]);
        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }
}
