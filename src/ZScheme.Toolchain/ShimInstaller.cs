using System.Runtime.InteropServices;

namespace ZScheme.Toolchain;

/// <summary>
///     Creates the <c>zs</c> and <c>zs-lsp</c> entries in <c>~/.zscheme/bin</c> that dispatch to the
///     selected toolchain. They are the same binary as <c>zsup</c>, which decides its role from
///     argv[0].
/// </summary>
/// <remarks>
///     Always re-stamped by <c>zsup install</c> and <c>zsup self update</c>, so the shims can never
///     drift out of sync with the <c>zsup</c> next to them.
/// </remarks>
public static partial class ShimInstaller
{
    /// <summary>The names installed alongside <c>zsup</c>.</summary>
    public static readonly string[] ShimNames = ["zs", "zs-lsp"];

    /// <summary>Owner rwx, group/other rx — the mode a published apphost normally carries.</summary>
    internal const UnixFileMode Executable755 =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherExecute;

    /// <summary>
    ///     Stamps a shim for every name in <see cref="ShimNames" /> next to
    ///     <paramref name="zsupPath" />'s installed location.
    /// </summary>
    /// <returns>The paths that were written.</returns>
    public static IReadOnlyList<string> Install(string binDir, string zsupPath)
    {
        Directory.CreateDirectory(binDir);

        var written = new List<string>();
        foreach (var name in ShimNames)
        {
            var target = Path.Combine(binDir, ZSchemeHome.ExeName(name));
            InstallOne(zsupPath, target);
            written.Add(target);
        }

        return written;
    }

    private static void InstallOne(string zsupPath, string shimPath)
    {
        // Replacing rather than updating in place: on Unix a hardlink to the old inode would
        // otherwise survive, and on Windows the copy would fail against an existing file.
        if (File.Exists(shimPath))
            File.Delete(shimPath);

        if (OperatingSystem.IsWindows())
        {
            // Symlinks need admin or Developer Mode, and a hardlink would be silently orphaned by
            // `zsup self update`'s rename-then-replace. A copy always works.
            File.Copy(zsupPath, shimPath, overwrite: true);
            return;
        }

        // A hardlink makes both dispatch signals agree: argv[0] is the invoked name, and
        // /proc/self/exe resolves to this path rather than to zsup.
        if (Link(zsupPath, shimPath) == 0)
        {
            MakeExecutable(shimPath);
            return;
        }

        // Different filesystem, or a filesystem without hardlinks. A relative target keeps the
        // whole home directory movable.
        File.CreateSymbolicLink(shimPath, Path.GetFileName(zsupPath));
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
