using ZScheme.Toolchain;

namespace ZScheme.Zsup;

internal static class ZsupHelpers
{
    /// <summary>
    ///     The working directory to search for a <c>.zscheme-version</c> pin, or <c>null</c> when
    ///     there is none.
    /// </summary>
    /// <remarks>
    ///     On Unix a process keeps running with its working directory unlinked — a scratch directory
    ///     a sibling build step cleaned, a tree `git clean` removed, a worktree pruned under a
    ///     running language server — and <c>getcwd</c> then fails with <c>ENOENT</c>, which .NET
    ///     surfaces as a <see cref="DirectoryNotFoundException" />. Answering rather than throwing
    ///     matters most on the shim path, which every <c>zs</c> invocation takes and where zsup's
    ///     compiled-out stack traces leave one bare line as the whole diagnosis. "No directory to
    ///     search" is indistinguishable from "no pin found", so resolution simply falls through to
    ///     the global default.
    /// </remarks>
    internal static string? CurrentDirectoryOrNull()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a message to stderr and returns the failure exit code.</summary>
    internal static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    /// <summary>Writes a multi-line message (as produced by the resolution formatter) to stderr.</summary>
    internal static int Error(params string[] lines)
    {
        foreach (var line in lines)
            Console.Error.WriteLine(line);

        return 1;
    }

    /// <summary>Writes an advisory to stderr without failing the command.</summary>
    internal static void Warn(string message)
    {
        Console.Error.WriteLine($"warning: {message}");
    }

    /// <summary>Reports every shim that could not be re-stamped, one name per line.</summary>
    /// <remarks>
    ///     Named individually because the drift is otherwise invisible: the shims are stamped in
    ///     order, so a lock on <c>zs</c> leaves <c>zs-lsp</c> on the previous zsup, and the next
    ///     thing the user sees is a language-server bug that was fixed two releases ago. Whether the
    ///     name is stale or gone is read back off the filesystem rather than assumed --
    ///     <c>zsup install</c> renames the new shim over the old one, so the old shim survives a
    ///     failure, while <c>zsup self update</c> renames every shim aside first, so a failure
    ///     leaves nothing.
    /// </remarks>
    /// <param name="recovery">
    ///     The command that re-stamps the shims from where the caller stands, spelled exactly as the
    ///     user has to type it. It differs per caller and neither form is a default: after an
    ///     install only that toolchain's <c>--force</c> reinstall stamps them again, and after a
    ///     self update a bare <c>zsup self update</c> would report itself already current and do
    ///     nothing at all.
    /// </param>
    internal static void WarnAboutUnstampedShims(ShimInstaller.Result stamped, string recovery)
    {
        foreach (var failure in stamped.Failed)
        {
            Warn($"could not refresh `{failure.Name}`: {failure.Message}");
            Console.Error.WriteLine(
                File.Exists(failure.Path)
                    ? $"help: {failure.Path} still points at the previous zsup; close whatever is "
                        + $"using it and run `{recovery}`"
                    : $"help: {failure.Path} is missing; close whatever is holding it and run "
                        + $"`{recovery}`"
            );
        }
    }

    /// <summary>
    ///     Deletes a download slot and the archive in it, ignoring a failure to do so.
    /// </summary>
    /// <remarks>
    ///     Every caller is cleaning up an archive it no longer needs, either after the install it
    ///     fed has committed or alongside the error that rejected it. In both positions the delete
    ///     is the least important thing happening, so it must not become the reported outcome --
    ///     an antivirus scanner still holding a freshly written file is routine on Windows. A
    ///     leftover is swept by <see cref="ToolchainInstaller.SweepTransients" /> on a later run
    ///     and is never trusted in the meantime: nothing but the process that created the slot ever
    ///     looks inside it.
    /// </remarks>
    internal static void TryDeleteDownloadSlot(string slot)
    {
        try
        {
            if (Directory.Exists(slot))
                Directory.Delete(slot, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Swept later.
        }
    }
}
