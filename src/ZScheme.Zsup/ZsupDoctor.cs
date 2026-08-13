using System.Diagnostics;
using ZScheme.Toolchain;

namespace ZScheme.Zsup;

/// <summary>
///     Post-install advisories. These only ever warn: PATH edits belong to the bootstrap scripts,
///     and the user may well install the .NET runtime after the toolchain.
/// </summary>
internal static class ZsupDoctor
{
    internal static void WarnIfBinDirNotOnPath(string? home = null)
    {
        WarnIfBinDirNotOnPath(home, Environment.GetEnvironmentVariable("PATH"));
    }

    /// <summary>
    ///     Overload taking the <c>PATH</c> value explicitly, so no test ever has to write to the
    ///     process environment — mirroring <c>ZSchemeHome.GetHome</c>'s testable overload.
    /// </summary>
    internal static void WarnIfBinDirNotOnPath(string? home, string? pathValue)
    {
        // Guarded like the comparison below, and for the same reason it is: this runs at
        // InstallCommand.cs:123, after the toolchain has been installed, after `installed toolchain
        // '...'` has been printed and after the default has been recorded -- so an escaping
        // exception turns a completed install into a bare line and a non-zero exit, and scripts
        // keyed on that exit code read a finished install as a failure. WarnIfRuntimeMissing's own
        // catch documents that hazard; this advisory took the same lesson. An over-long or
        // unparseable ZSCHEME_HOME is a problem the install itself would already have reported.
        if (FullPathOrNull(ZSchemeHome.GetBinDir(home)) is not { } binDir)
            return;

        var onPath = (pathValue ?? "")
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Any(p => PathsEqual(p, binDir));

        if (onPath)
            return;

        ZsupHelpers.Warn($"{binDir} is not on your PATH; `zs` will not resolve to a toolchain");
        Console.Error.WriteLine(
            OperatingSystem.IsWindows()
                ? "help: open a new terminal, or add it to your user PATH"
                : $"help: run `. \"{ZSchemeHome.GetEnvFile(home)}\"`, or open a new shell"
        );
    }

    /// <summary>
    ///     Compares two directory paths, ignoring a trailing separator and — on Windows — case.
    ///     A path neither side can resolve is not a match.
    /// </summary>
    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return FullPathOrNull(a) is { } left
            && FullPathOrNull(b) is { } right
            && string.Equals(left, right, comparison);
    }

    /// <summary>
    ///     <paramref name="path" /> made absolute and stripped of a trailing separator, or
    ///     <c>null</c> when the OS will not parse it.
    /// </summary>
    /// <remarks>
    ///     The triple every other path-parsing site in zsup catches — <c>ZSchemeHome.IsBinDir</c>,
    ///     <c>ToolchainRegistry.ReadLinkTarget</c>, <c>ToolchainInstaller.FullPathOrNull</c>,
    ///     <c>LinkCommand</c>'s own normalization. Only <see cref="ArgumentException" /> was caught
    ///     here, and <see cref="PathTooLongException" /> derives from <see cref="IOException" />
    ///     rather than from it, so one over-long entry in the user's PATH escaped.
    /// </remarks>
    private static string? FullPathOrNull(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e)
            when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Checks for a .NET 10 runtime, which <c>zs</c> and <c>zs-lsp</c> need (they ship
    ///     framework-dependent). zsup itself is native and does not.
    /// </summary>
    internal static void WarnIfRuntimeMissing()
    {
        switch (TryListRuntimes())
        {
            case null:
                ZsupHelpers.Warn("could not find `dotnet` on your PATH");
                Console.Error.WriteLine(
                    "help: ZScheme needs the .NET 10 runtime: https://dotnet.microsoft.com/download"
                );
                break;
            case { } runtimes when !runtimes.Contains("Microsoft.NETCore.App 10."):
                ZsupHelpers.Warn("no .NET 10 runtime found");
                Console.Error.WriteLine(
                    "help: ZScheme needs the .NET 10 runtime: https://dotnet.microsoft.com/download"
                );
                break;
        }
    }

    private static string? TryListRuntimes()
    {
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo("dotnet", "--list-runtimes")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            );

            if (process is null)
                return null;

            // Both pipes must be drained concurrently. Reading stdout to end while stderr fills its
            // buffer would deadlock -- a misconfigured DOTNET_ROOT makes the host chatty enough to
            // hit that, and it would hang `zsup install` after it had already succeeded.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            // The bound has to be honoured, not just requested. A grandchild that inherited the
            // pipe keeps it open after `dotnet` itself has exited, so the reads can still be
            // pending here -- and `stdout.Result` on a pending task blocks with no timeout at all,
            // which is the very hang this drain is bounded to avoid. Disposing the process on the
            // way out closes our ends and lets the abandoned reads finish.
            if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(5)))
                return null;

            return process.ExitCode == 0 ? stdout.Result : null;
        }
        catch (Exception e)
            when (e
                    is System.ComponentModel.Win32Exception
                        or InvalidOperationException
                        // What both Task.WaitAll and stdout.Result raise for a faulted pipe read,
                        // and it is neither of the others. This runs after `zsup install` has
                        // committed and printed success, and neither InstallCommand nor
                        // SelfCommand catches it -- so an unhandled one turns a completed install
                        // into a bare exception line and a non-zero exit. A runtime check that
                        // cannot answer is exactly the "return null" case above.
                        or AggregateException
            )
        {
            return null;
        }
    }
}
