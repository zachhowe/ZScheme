using System.Runtime.InteropServices;

namespace ZScheme.Toolchain;

/// <summary>
///     Creates the <c>zs</c> and <c>zs-lsp</c> entries in <c>~/.zscheme/bin</c> that dispatch to the
///     selected toolchain. They are the same binary as <c>zsup</c>, which decides its role from
///     argv[0].
/// </summary>
/// <remarks>
///     Always re-stamped by <c>zsup install</c> and <c>zsup self update</c>, so the shims can never
///     drift out of sync with the <c>zsup</c> next to them. Where the filesystem refuses -- a shim
///     locked by a process still running it -- <see cref="Install" /> names the shim it could not
///     re-stamp instead of letting the drift pass unreported.
/// </remarks>
public static partial class ShimInstaller
{
    /// <summary>The names installed alongside <c>zsup</c>.</summary>
    public static readonly string[] ShimNames = ["zs", "zs-lsp"];

    /// <summary>
    ///     The canonical shim name matching <paramref name="invokedAs" />, or <c>null</c> when it is
    ///     not one of them.
    /// </summary>
    /// <remarks>
    ///     Matched the way the filesystem resolves names, because that is where the name comes from:
    ///     typing <c>ZS</c> on Windows launches <c>zs.exe</c>, and argv[0] then carries whatever case
    ///     the user typed. An ordinal match there would miss the shim branch entirely and drop the
    ///     user into the manager CLI.
    /// </remarks>
    public static string? MatchName(string? invokedAs)
    {
        return Array.Find(ShimNames, name => ToolchainName.AreSame(name, invokedAs));
    }

    /// <summary>Owner rwx, group/other rx — the mode a published apphost normally carries.</summary>
    internal const UnixFileMode Executable755 =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherExecute;

    /// <param name="Name">The shim name, as it appears in <see cref="ShimNames" />.</param>
    /// <param name="Path">Where that shim lives.</param>
    /// <param name="Message">Why it could not be stamped.</param>
    public sealed record Failure(string Name, string Path, string Message);

    /// <param name="Written">The paths that now hold the current zsup.</param>
    /// <param name="Failed">
    ///     One entry per name that could not be stamped and is therefore stale or absent. Empty on
    ///     the normal path; a caller that reports nothing when it is not leaves the drift silent.
    /// </param>
    public sealed record Result(IReadOnlyList<string> Written, IReadOnlyList<Failure> Failed);

    /// <summary>
    ///     Stamps a shim for every name in <see cref="ShimNames" /> next to
    ///     <paramref name="zsupPath" />'s installed location.
    /// </summary>
    /// <remarks>
    ///     Every name is attempted even when an earlier one fails, because on Windows any shim may
    ///     be locked by a process still running it -- an editor holding <c>zs-lsp</c>, a build in
    ///     another terminal holding <c>zs</c>. Stopping at the first lock would stamp a prefix of
    ///     the names and leave the rest pointing at the previous zsup, which is the mixed-version
    ///     state this class exists to rule out.
    /// </remarks>
    public static Result Install(string binDir, string zsupPath)
    {
        Directory.CreateDirectory(binDir);

        var written = new List<string>();
        var failed = new List<Failure>();

        foreach (var name in ShimNames)
        {
            var target = Path.Combine(binDir, ZSchemeHome.ExeName(name));
            try
            {
                InstallOne(zsupPath, target);
                written.Add(target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(new Failure(name, target, e.Message));
            }
        }

        return new Result(written, failed);
    }

    private static void InstallOne(string zsupPath, string shimPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // Symlinks need admin or Developer Mode, and a hardlink would be silently orphaned by
            // `zsup self update`'s rename-then-replace. A copy always works.
            //
            // Overwritten in place rather than deleted first: against a locked shim the copy fails
            // exactly as the delete would, but it fails without having destroyed what was already
            // there, so a locked name degrades to "still the old zsup" instead of "gone".
            File.Copy(zsupPath, shimPath, overwrite: true);
            return;
        }

        // Replaced rather than updated in place: a hardlink to the old inode would otherwise
        // survive. Safe to delete first here, because Unix removes only the directory entry and a
        // process running the old image keeps it.
        if (File.Exists(shimPath))
            File.Delete(shimPath);

        // A hardlink makes both dispatch signals agree: argv[0] is the invoked name, and
        // /proc/self/exe resolves to this path rather than to zsup.
        if (Link(zsupPath, shimPath) == 0)
        {
            MakeExecutable(shimPath);
            return;
        }

        // Different filesystem, or a filesystem without hardlinks. A relative target keeps the
        // whole home directory movable -- relative to the shim's own directory, since zsup does not
        // have to be the one sitting next to it.
        var relative = Path.GetRelativePath(Path.GetDirectoryName(shimPath)!, zsupPath);
        File.CreateSymbolicLink(shimPath, relative);
    }

    /// <summary>Sets mode 0755 on Unix; a no-op on Windows.</summary>
    public static void MakeExecutable(string path)
    {
        // The guard is required: File.SetUnixFileMode throws PlatformNotSupportedException on
        // Windows, and it is also what satisfies the CA1416 platform analyzer.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, Executable755);
    }

    [LibraryImport(
        "libc",
        EntryPoint = "link",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8
    )]
    private static partial int Link(string existing, string created);
}
