namespace ZScheme.Toolchain;

/// <summary>
///     Validation for toolchain names. A name becomes a directory under
///     <c>~/.zscheme/toolchains/</c>, and it can come from an untrusted source — a
///     <c>.zscheme-version</c> file checked into someone else's repository, for instance — so a
///     name containing <c>..</c> or a separator must never be able to escape that directory.
///     <see cref="ZSchemeHome.GetToolchainDir" /> enforces this itself rather than trusting callers.
/// </summary>
public static class ToolchainName
{
    /// <summary>
    ///     How two toolchain names are compared. Names are directory names, so they must be compared
    ///     the way the filesystem resolves them — otherwise `zsup uninstall dev` would delete the
    ///     directory `Dev` on Windows or macOS while leaving it as the recorded default.
    /// </summary>
    public static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Comparer matching <see cref="Comparison" />.</summary>
    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>True when both names select the same toolchain.</summary>
    public static bool AreSame(string? a, string? b)
    {
        return string.Equals(a, b, Comparison);
    }

    /// <summary>Characters that are never legal in a toolchain name, whatever the platform.</summary>
    private static readonly char[] Forbidden = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Trailing/leading whitespace would produce a directory that is painful to remove on
        // Windows, and a leading '.' would collide with the transient .staging-*/.trash-* entries.
        if (name != name.Trim() || name.StartsWith('.'))
            return false;

        // A trailing '.' is stripped by Win32 when the name becomes a path component, so `0.4.0.`
        // and `0.4.0` name one directory while comparing as two different toolchains. That gap is
        // enough for `zsup install 0.4.0. --from …` to pass ExplainNameTaken and overwrite `0.4.0`
        // without --force, and for `zsup uninstall 0.4.0.` to delete its directory while leaving
        // `0.4.0` recorded as the default -- which breaks every later `zs`. Rejected rather than
        // trimmed: a name that does not survive round-tripping through a path is not a name.
        if (name.EndsWith('.'))
            return false;

        if (name.IndexOfAny(Forbidden) >= 0)
            return false;

        // ".." anywhere as a whole segment; the separator check above already rules out multi-
        // segment names, so this is really just the bare "..".
        if (name == "..")
            return false;

        return !name.Any(char.IsControl);
    }

    /// <summary>
    ///     Makes a name that arrived from outside safe to echo, or returns <c>null</c> if nothing
    ///     survives.
    /// </summary>
    /// <remarks>
    ///     Both sources of a selected name are attacker-controlled in the same way — a
    ///     <c>.zscheme-version</c> checked into whatever repository the user cloned, and the
    ///     <c>ZSCHEME_VERSION</c> that repository's direnv or CI config exports — and an invalid one
    ///     is reported straight back to the terminal by <c>ResolutionErrorFormatter</c>. Control
    ///     characters (ANSI/OSC escapes) are therefore stripped and the length is bounded before the
    ///     value goes anywhere.
    ///     <para>
    ///         A value that was nothing but control characters sanitizes to the empty string, which
    ///         is not a toolchain name: returning it would have <c>VersionFileLocator</c> report a
    ///         hit and fail every command from that directory with <c>toolchain '' is not
    ///         installed</c>. Null instead, so it behaves like the blank line it effectively is.
    ///     </para>
    /// </remarks>
    public static string? Sanitize(string? value)
    {
        const int maxLength = 64;

        if (value is null)
            return null;

        // Trimmed after stripping rather than before: an escape sequence wrapped in spaces would
        // otherwise leave the spaces behind once its control characters were removed.
        var cleaned = new string([.. value.Where(c => !char.IsControl(c))]).Trim();
        if (cleaned.Length == 0)
            return null;

        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }

    /// <summary>
    ///     Throws when <paramref name="name" /> is not a valid toolchain name. Used at the point
    ///     where a name is turned into a filesystem path.
    /// </summary>
    public static string Validate(string? name, string paramName = "name")
    {
        if (!IsValid(name))
            throw new ArgumentException($"Invalid toolchain name: '{name}'", paramName);

        return name!;
    }
}
