using ZScheme.Zsup.Commands;

namespace ZScheme.Zsup;

/// <summary>Manager-mode dispatch, mirroring the <c>zs</c> CLI's hand-rolled style.</summary>
internal static class ZsupCli
{
    internal static int Run(string[] args)
    {
        if (args.Length == 0)
            return PrintUsage();

        return args[0] switch
        {
            "install" => InstallCommand.Run(args[1..]),
            "use" => UseCommand.Run(args[1..]),
            "list" => ListCommand.Run(args[1..]),
            "uninstall" => UninstallCommand.Run(args[1..]),
            "link" => LinkCommand.RunLink(args[1..]),
            "unlink" => LinkCommand.RunUnlink(args[1..]),
            "which" => WhichCommand.Run(args[1..]),
            "self" => SelfCommand.Run(args[1..]),
            "--version" or "-v" => PrintVersion(),
            "--help" or "-h" => PrintUsage(),
            _ => ZsupHelpers.Error($"error: unknown command: {args[0]}"),
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"zsup {ZsupVersion.Value}");
        return 0;
    }

    private static int PrintUsage()
    {
        Console.WriteLine($"zsup {ZsupVersion.Value} - the ZScheme toolchain manager");
        Console.WriteLine();
        Console.WriteLine("Usage: zsup <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine(
            "  install <version>       Install a toolchain (use 'latest' for the newest release)"
        );
        Console.WriteLine("  use <toolchain>         Select the default toolchain");
        Console.WriteLine("  list                    List installed toolchains");
        Console.WriteLine("  uninstall <toolchain>   Remove an installed toolchain");
        Console.WriteLine("  link <name> <dir>       Register a locally built tree as a toolchain");
        Console.WriteLine("  unlink <name>           Remove a linked toolchain");
        Console.WriteLine("  which [zs|zs-lsp]       Print the resolved path of a tool");
        Console.WriteLine("  self update             Update zsup itself");
        Console.WriteLine("  --version, -v           Print the zsup version");
        Console.WriteLine();
        Console.WriteLine("Options (install):");
        Console.WriteLine(
            "  --from <archive|dir>    Install from a local archive or directory instead of downloading"
        );
        Console.WriteLine("  --force                 Replace an already-installed toolchain");
        Console.WriteLine("  --no-default            Do not make this the default toolchain");
        Console.WriteLine();
        Console.WriteLine("Options (use):");
        Console.WriteLine(
            "  --local                 Pin in ./.zscheme-version instead of setting the global default"
        );
        Console.WriteLine();
        Console.WriteLine("Options (uninstall):");
        Console.WriteLine(
            "  --purge-cache           Also delete the toolchain's compiled package cache"
        );
        Console.WriteLine();
        Console.WriteLine("Toolchain selection, highest priority first:");
        Console.WriteLine("  ZSCHEME_VERSION         Environment variable");
        Console.WriteLine(
            "  .zscheme-version        Nearest such file at or above the current directory"
        );
        Console.WriteLine("  zsup use <toolchain>    The global default");
        Console.WriteLine();
        Console.WriteLine("Environment variables:");
        Console.WriteLine(
            "  ZSCHEME_HOME            Root for toolchains and caches (default: ~/.zscheme)"
        );
        Console.WriteLine("  ZSCHEME_VERSION         Toolchain to use for this invocation");
        Console.WriteLine("  ZSCHEME_GITHUB_REPO     Repository to fetch releases from");
        Console.WriteLine("  ZSCHEME_DIST_BASE_URL   Base URL for release downloads");
        Console.WriteLine("  ZSCHEME_GITHUB_API_URL  API base URL used to resolve `latest`");
        return 0;
    }
}
