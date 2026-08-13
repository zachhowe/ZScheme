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
    ///     Suffix of the private slot a Windows shim is staged in before it is renamed into place.
    /// </summary>
    private const string StagingSuffix = ".tmp-";

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

    /// <summary>
    ///     Puts the current zsup at <paramref name="shimPath" />.
    /// </summary>
    /// <remarks>
    ///     Both platforms build the shim under a private name beside its destination and rename it
    ///     over, so the only step that touches the shim is one that either replaces it or leaves it
    ///     exactly as it was. On Windows the hazard is a copy that dies part-way: CopyFile truncates
    ///     before it writes, so a full disk or a network home dropping out would leave a zs.exe that
    ///     is neither the old zsup nor the new one. On Unix it is a filesystem that supports neither
    ///     hardlinks nor symlinks — exFAT, some SMB mounts — where deleting first and creating after
    ///     leaves no shim at all, and the caller only warns, so the user is told their <c>zs</c> is
    ///     stale when it is in fact gone. The rename also gives the "a hardlink to the old inode
    ///     must not survive" property the delete was there for: it replaces the directory entry
    ///     outright. Against a locked name it fails exactly as the copy would, so a locked shim
    ///     still degrades to "still the old zsup" rather than "missing".
    /// </remarks>
    private static void InstallOne(string zsupPath, string shimPath)
    {
        SweepStaging(shimPath);

        var staged = shimPath + StagingSuffix + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Build(zsupPath, staged, shimPath);
            File.Move(staged, shimPath, overwrite: true);
        }
        finally
        {
            // A no-op once the rename consumed it.
            TryDeleteFile(staged);
        }
    }

    /// <summary>
    ///     Materializes the shim payload at <paramref name="staged" />.
    /// </summary>
    /// <param name="finalPath">
    ///     Where it is about to be renamed. Only the symlink needs it, and only for its directory:
    ///     the staging slot is that directory's own child, so a target relative to it stays correct
    ///     across the rename.
    /// </param>
    private static void Build(string zsupPath, string staged, string finalPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // Symlinks need admin or Developer Mode, and a hardlink would be silently orphaned by
            // `zsup self update`'s rename-then-replace. A copy always works.
            File.Copy(zsupPath, staged, overwrite: true);
            return;
        }

        // A hardlink makes both dispatch signals agree: argv[0] is the invoked name, and
        // /proc/self/exe resolves to this path rather than to zsup.
        if (Link(zsupPath, staged) == 0)
        {
            MakeExecutable(staged);
            return;
        }

        // Different filesystem, or a filesystem without hardlinks. A relative target keeps the
        // whole home directory movable -- relative to the shim's own directory, since zsup does not
        // have to be the one sitting next to it.
        var relative = Path.GetRelativePath(Path.GetDirectoryName(finalPath)!, zsupPath);
        File.CreateSymbolicLink(staged, relative);
    }

    /// <summary>
    ///     Deletes staging slots older than <see cref="ZSchemeHome.StagingMaxAge" /> beside
    ///     <paramref name="shimPath" />.
    /// </summary>
    /// <remarks>
    ///     The <c>finally</c> around the rename covers a failure, but not a kill between building
    ///     the slot and the rename. Nothing else walks the bin directory looking for these, so
    ///     without a sweep they would pile up one zsup-sized file at a time.
    ///     <para>
    ///         Age-gated, because a slot this sweep can see is not necessarily debris: a concurrent
    ///         zsup stamping the same shim has one of its own, and deleting it leaves that process
    ///         renaming a path that no longer exists. It then reports a shim it could not refresh —
    ///         naming the exact drift this class exists to rule out, for a shim the other process
    ///         went on to stamp perfectly — and tells the user to close whatever is holding a file
    ///         nothing is holding.
    ///     </para>
    /// </remarks>
    private static void SweepStaging(string shimPath)
    {
        var cutoff = DateTime.UtcNow - ZSchemeHome.StagingMaxAge;

        try
        {
            foreach (
                var stale in Directory.EnumerateFiles(
                    Path.GetDirectoryName(shimPath)!,
                    Path.GetFileName(shimPath) + StagingSuffix + "*"
                )
            )
                if (File.GetLastWriteTimeUtc(stale) < cutoff)
                    TryDeleteFile(stale);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a stale slot costs disk, not correctness, and must never be the reason
            // a shim was left un-stamped.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Either the rename already consumed it, or something else holds it and the sweep on
            // the next stamp picks it up.
        }
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
