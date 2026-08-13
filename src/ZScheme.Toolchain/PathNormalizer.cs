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
    ///     null, empty, whitespace, or something the OS will not parse as a path.
    /// </summary>
    /// <remarks>
    ///     Answering rather than throwing is what every caller needs: each treats <c>null</c> as
    ///     "this override is not usable" and falls through to the next source, so a misconfigured
    ///     <c>ZSCHEME_HOME</c> degrades to <c>~/.zscheme</c> instead of taking down every <c>zsup</c>
    ///     command and every <c>zs</c> invocation with it. This matches
    ///     <see cref="ZSchemeHome.IsBinDir" /> and <c>ToolchainInstaller.FullPathOrNull</c>, which
    ///     answer the same way for the same reason.
    ///     <para>
    ///         The empty case is easy to reach rather than theoretical: the blank check runs before
    ///         the expansion, and a <c>%VAR%</c> reference to a variable set to the empty string
    ///         expands to nothing.
    ///     </para>
    /// </remarks>
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

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception e)
            when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }
}
