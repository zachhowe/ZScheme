using ZScheme.Toolchain;

namespace ZScheme.Zsup;

/// <summary>
///     Entry point for both roles this binary plays. Installed as <c>zsup</c> it manages toolchains;
///     hardlinked or copied to <c>zs</c> / <c>zs-lsp</c> it acts as a shim and hands off to the
///     selected toolchain.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // Explicit override, mostly for tests and for any platform where argv[0] cannot be
        // trusted: `zsup --shim zs -- <args...>`.
        if (args is ["--shim", var forced, ..])
        {
            var rest = args[2..];
            if (rest is ["--", ..])
                rest = rest[1..];

            return ShimInstaller.MatchName(forced) is { } forcedShim
                ? ShimRunner.Run(forcedShim, rest)
                : ZsupHelpers.Error($"error: unknown shim '{forced}'");
        }

        // The canonical name rather than what was typed: the rest of the shim path turns it into a
        // file name and an argv[0] for the child.
        if (ShimInstaller.MatchName(GetInvokedName()) is { } shim)
            return ShimRunner.Run(shim, args);

        // Only in manager mode: the shim path stays as short as possible, and a leftover binary
        // from a previous `self update` is harmless until zsup runs again anyway.
        ZsupSelf.SweepStaleBinaries();

        return ZsupCli.Run(args);
    }

    /// <summary>
    ///     The name this process was invoked under.
    /// </summary>
    /// <remarks>
    ///     argv[0] is preferred over <see cref="Environment.ProcessPath" />: on Linux the latter
    ///     reads <c>/proc/self/exe</c>, which resolves symlinks and would therefore always report
    ///     <c>zsup</c> even when the user typed <c>zs</c>. With the hardlinks and copies that
    ///     <c>ShimInstaller</c> creates, both signals agree — but the symlink fallback exists, so
    ///     the order matters.
    /// </remarks>
    private static string GetInvokedName()
    {
        var argv = Environment.GetCommandLineArgs();
        var argv0 = argv.Length > 0 && argv[0].Length > 0 ? argv[0] : Environment.ProcessPath;

        return argv0 is null ? "zsup" : Path.GetFileNameWithoutExtension(argv0);
    }
}
