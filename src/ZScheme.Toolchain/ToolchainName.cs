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

        if (name.IndexOfAny(Forbidden) >= 0)
            return false;

        // ".." anywhere as a whole segment; the separator check above already rules out multi-
        // segment names, so this is really just the bare "..".
        if (name == "..")
            return false;

        return !name.Any(char.IsControl);
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
