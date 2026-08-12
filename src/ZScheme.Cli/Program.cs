using Serilog;
using Serilog.Events;
using ZScheme.Compiler;
using ZScheme.Compiler.Cache;
using ZScheme.Toolchain;

namespace ZScheme.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var debug = args.Contains("--debug");
        if (debug)
        {
            args = args.Where(a => a != "--debug").ToArray();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}",
                    standardErrorFromLevel: LogEventLevel.Verbose
                )
                .CreateLogger();
        }

        // Read only to report it. ZSchemePaths resolves the variable itself, so `zs`, `zs-lsp` and
        // every other host land on the same cache root without each having to opt in -- which is
        // what went wrong when this was the CLI's job alone.
        var envCacheDir = Environment.GetEnvironmentVariable(
            ZSchemeHome.CacheDirEnvironmentVariable
        );
        if (!string.IsNullOrWhiteSpace(envCacheDir))
            Log.Debug(
                "CLI: ZSCHEME_CACHE_DIR override active, cache root={CacheRoot}",
                ZSchemePaths.GetCacheRoot()
            );

        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            var command = args[0];
            Log.Debug("CLI: command={Command}, args={Args}", command, string.Join(" ", args[1..]));
            return command switch
            {
                "compile" => CompileCommand.Run(args[1..]),
                "build" => BuildCommand.Run(args[1..]),
                "install" => InstallCommand.Run(args[1..]),
                "test" => await TestCommand.RunAsync(args[1..]),
                "run" => ExecuteCommand.Run(args[1..]),
                "lint" => LintCommand.Run(args[1..]),
                "repl" => ReplCommand.Run(),
                "package" => PackageCommand.Run(args[1..]),
                "generate-project" => GenerateProjectCommand.Run(args[1..]),
                "--version" or "-v" => PrintVersion(),
                "--help" or "-h" => PrintUsage(),
                _ => CliHelpers.Error($"Unknown command: {command}"),
            };
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"ZScheme Compiler {CompilerInfo.VersionString}");
        return 0;
    }

    private static int PrintUsage()
    {
        Console.WriteLine($"ZScheme Compiler {CompilerInfo.VersionString}");
        Console.WriteLine();
        Console.WriteLine("Usage: zs <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Global options:");
        Console.WriteLine(
            "  --debug                 Enable compiler debug logging (output to stderr)"
        );
        Console.WriteLine();
        Console.WriteLine("Environment variables:");
        Console.WriteLine(
            "  ZSCHEME_CACHE_DIR       Override base directory for ZScheme caches (pkg/ + git/);"
        );
        Console.WriteLine(
            "                          defaults to <home>/cache. NuGet cache is unaffected."
        );
        Console.WriteLine(
            "  ZSCHEME_HOME            Root for toolchains and caches (default: ~/.zscheme)"
        );
        Console.WriteLine(
            "  ZSCHEME_VERSION         Toolchain the `zs` shim runs; see `zsup --help`"
        );
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  compile <file.zs>       Compile a ZScheme file");
        Console.WriteLine("  build                   Build from a .zspkg package manifest");
        Console.WriteLine("  install                 Compile a library package and cache it");
        Console.WriteLine("  test                    Run package tests defined in manifest");
        Console.WriteLine("  run <file.zs>           Compile and run a ZScheme file");
        Console.WriteLine("  lint [paths...]         Report (or fix) style diagnostics");
        Console.WriteLine("  repl                    Start interactive REPL");
        Console.WriteLine("  package <cmd>           Package management (init)");
        Console.WriteLine("  generate-project        Generate a .csproj project directory");
        Console.WriteLine();
        Console.WriteLine("Options (compile):");
        Console.WriteLine("  --output, -o <path>     Output path (default: output)");
        Console.WriteLine("  --backend, -b cs|il     Backend (default: cs)");
        Console.WriteLine(
            "  --ref <dir>             Directory containing CLR assemblies (repeatable)"
        );
        Console.WriteLine(
            "  --module-path <dir>     Additional module search directory (repeatable)"
        );
        Console.WriteLine(
            "  --package-path <dir>    Register a package for qualified imports (repeatable)"
        );
        Console.WriteLine("  --precompiled <path>    Reference a precompiled .dll (repeatable)");
        Console.WriteLine();
        Console.WriteLine("Options (build):");
        Console.WriteLine(
            "  --manifest, -m <path>   Path to .zspkg manifest (default: auto-detect)"
        );
        Console.WriteLine("  --output, -o <path>     Output path (overrides manifest)");
        Console.WriteLine("  --backend, -b cs|il     Backend (overrides manifest)");
        Console.WriteLine("  --ref <dir>             Assembly search directory (repeatable)");
        Console.WriteLine(
            "  --module-path <dir>     Additional module search directory (repeatable)"
        );
        Console.WriteLine(
            "  --package-path <dir>    Register a package for qualified imports (repeatable)"
        );
        Console.WriteLine("  --precompiled <path>    Reference a precompiled .dll (repeatable)");
        Console.WriteLine();
        Console.WriteLine("Options (install):");
        Console.WriteLine(
            "  --manifest, -m <path>   Path to .zspkg manifest (default: auto-detect)"
        );
        Console.WriteLine(
            "  --package-path <dir>    Register a package for qualified imports (repeatable)"
        );
        Console.WriteLine();
        Console.WriteLine("Options (test):");
        Console.WriteLine(
            "  --manifest, -m <path>   Path to .zspkg manifest (default: auto-detect)"
        );
        Console.WriteLine(
            "  --module-path <dir>     Additional module search directory (repeatable)"
        );
        Console.WriteLine(
            "  --package-path <dir>    Register a package for qualified imports (repeatable)"
        );
        Console.WriteLine();
        Console.WriteLine("Options (lint):");
        Console.WriteLine("  --manifest, -m <path>   Package context for the files being linted");
        Console.WriteLine(
            "                          (default: the package owning each path, else auto-detect)"
        );
        Console.WriteLine("  --fix                   Rewrite the files in place");
        Console.WriteLine(
            "  --ref <dir>             Directory containing CLR assemblies (repeatable)"
        );
        Console.WriteLine(
            "  --module-path <dir>     Additional module search directory (repeatable)"
        );
        Console.WriteLine(
            "  --package-path <dir>    Register a package for qualified imports (repeatable)"
        );
        Console.WriteLine(
            "  [paths...]              Files or directories (default: the current package's sources)"
        );
        Console.WriteLine();
        Console.WriteLine("Options (package init):");
        Console.WriteLine("  --name <name>           Package name (default: directory name)");
        Console.WriteLine("  --version <version>     Version (default: 0.1.0)");
        Console.WriteLine("  --import-prefix <pfx>   Import prefix (default: name)");
        Console.WriteLine("  --description <desc>    Package description");
        Console.WriteLine("  --license <license>     License identifier");
        Console.WriteLine(
            "  --output, -o <dir>      Target directory (default: current directory)"
        );
        return 0;
    }
}
