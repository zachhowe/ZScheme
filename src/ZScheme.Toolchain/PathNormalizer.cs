namespace ZScheme.Toolchain;

/// <summary>
///     Normalizes user-supplied paths the way every ZScheme path override accepts them:
///     environment variables expanded, a leading <c>~</c> resolved to the user profile, and the
///     result made absolute.
/// </summary>
public static class PathNormalizer
{
    /// <summary>
    ///     Returns the normalized absolute path, or <c>null</c> when <paramref name="value" /> is
    ///     null, empty, or whitespace.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var expanded = Environment.ExpandEnvironmentVariables(trimmed);

        if (expanded.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (expanded.Length == 1)
                expanded = home;
            else if (expanded[1] == Path.DirectorySeparatorChar || expanded[1] == '/')
                expanded = Path.Combine(home, expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }
}
