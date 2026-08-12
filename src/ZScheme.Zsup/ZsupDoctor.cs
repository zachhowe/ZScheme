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
        var binDir = Path.GetFullPath(ZSchemeHome.GetBinDir(home));
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        var onPath = path.Split(Path.PathSeparator)
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
    /// </summary>
    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                comparison
            );
        }
        catch (ArgumentException)
        {
            // A malformed PATH entry simply is not a match.
            return false;
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
            when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
