namespace ZScript.Cli;

using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Package;
using ZScript.Compiler.Pipeline;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var command = args[0];
        return command switch
        {
            "compile" => RunCompile(args[1..]),
            "build" => RunBuild(args[1..]),
            "run" => RunExecute(args[1..]),
            "repl" => RunRepl(),
            "--version" or "-v" => PrintVersion(),
            "--help" or "-h" => PrintUsage(),
            _ => Error($"Unknown command: {command}")
        };
    }

    private static int RunCompile(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: zs compile <file.zs> [--output <path>] [--backend cs|il] [--stdlib <path>] [--ref <dir>]");
            return 1;
        }

        var filePath = args[0];
        var outputPath = "output";
        var backend = OutputMode.CSharp;
        string? stdlibPath = null;
        var assemblySearchPaths = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output" or "-o" when i + 1 < args.Length:
                    outputPath = args[++i];
                    break;
                case "--backend" or "-b" when i + 1 < args.Length:
                    backend = args[++i] == "il" ? OutputMode.IL : OutputMode.CSharp;
                    break;
                case "--stdlib" when i + 1 < args.Length:
                    stdlibPath = args[++i];
                    break;
                case "--ref" when i + 1 < args.Length:
                    assemblySearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
            }
        }

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            return 1;
        }

        var source = File.ReadAllText(filePath);
        var options = new CompilerOptions
        {
            OutputMode = backend,
            OutputPath = outputPath,
            StdLibPath = stdlibPath,
            AssemblySearchPaths = assemblySearchPaths
        };
        var compilation = new Compilation(options);
        var result = compilation.Compile(source, filePath);

        if (!result.Success)
        {
            foreach (var diag in result.Diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        if (backend == OutputMode.CSharp)
        {
            var outputFile = Path.ChangeExtension(outputPath, ".cs");
            File.WriteAllText(outputFile, result.Output);
            Console.WriteLine($"Generated: {outputFile}");
        }
        else
        {
            var extension = result.IsExecutable ? ".exe" : ".dll";
            var outputFile = Path.ChangeExtension(outputPath, extension);
            File.WriteAllBytes(outputFile, result.OutputBytes!);
            Console.WriteLine($"Generated: {outputFile}");

            if (result.IsExecutable)
            {
                var runtimeConfigFile = Path.ChangeExtension(outputFile, ".runtimeconfig.json");
                var version = Environment.Version;
                var runtimeConfig = $$"""
                    {
                      "runtimeOptions": {
                        "tfm": "net{{version.Major}}.{{version.Minor}}",
                        "framework": {
                          "name": "Microsoft.NETCore.App",
                          "version": "{{version.Major}}.{{version.Minor}}.0"
                        }
                      }
                    }
                    """;
                File.WriteAllText(runtimeConfigFile, runtimeConfig);
                Console.WriteLine($"Generated: {runtimeConfigFile}");
            }
        }

        return 0;
    }

    private static int RunBuild(string[] args)
    {
        string? manifestPath = null;
        var overrides = new CompilerOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest" or "-m" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    overrides.OutputPath = args[++i];
                    break;
                case "--backend" or "-b" when i + 1 < args.Length:
                    overrides.OutputMode = args[++i] == "il" ? OutputMode.IL : OutputMode.CSharp;
                    break;
                case "--stdlib" when i + 1 < args.Length:
                    overrides.StdLibPath = args[++i];
                    break;
                case "--ref" when i + 1 < args.Length:
                    overrides.AssemblySearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
            }
        }

        // Find manifest if not specified
        if (manifestPath is null)
        {
            var candidates = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.zspkg");
            if (candidates.Length == 0)
            {
                Console.Error.WriteLine("No .zspkg manifest found in current directory. Use --manifest to specify one.");
                return 1;
            }
            if (candidates.Length > 1)
            {
                Console.Error.WriteLine("Multiple .zspkg files found. Use --manifest to specify one.");
                return 1;
            }
            manifestPath = candidates[0];
        }

        var diagnostics = new DiagnosticBag();
        var builder = new PackageBuilder(diagnostics);
        var result = builder.Build(manifestPath, overrides);

        if (result is null || !result.Success)
        {
            var diags = result?.Diagnostics ?? diagnostics;
            foreach (var diag in diags.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        var outputPath = overrides.OutputPath != "output" ? overrides.OutputPath : "output";
        var backend = overrides.OutputMode;

        if (backend == OutputMode.CSharp || result.OutputBytes is null)
        {
            var outputFile = Path.ChangeExtension(outputPath, ".cs");
            File.WriteAllText(outputFile, result.Output);
            Console.WriteLine($"Generated: {outputFile}");
        }
        else
        {
            var extension = result.IsExecutable ? ".exe" : ".dll";
            var outputFile = Path.ChangeExtension(outputPath, extension);
            File.WriteAllBytes(outputFile, result.OutputBytes);
            Console.WriteLine($"Generated: {outputFile}");

            if (result.IsExecutable)
            {
                var runtimeConfigFile = Path.ChangeExtension(outputFile, ".runtimeconfig.json");
                var version = Environment.Version;
                var runtimeConfig = $$"""
                    {
                      "runtimeOptions": {
                        "tfm": "net{{version.Major}}.{{version.Minor}}",
                        "framework": {
                          "name": "Microsoft.NETCore.App",
                          "version": "{{version.Major}}.{{version.Minor}}.0"
                        }
                      }
                    }
                    """;
                File.WriteAllText(runtimeConfigFile, runtimeConfig);
                Console.WriteLine($"Generated: {runtimeConfigFile}");
            }
        }

        return 0;
    }

    private static int RunExecute(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: zs run <file.zs>");
            return 1;
        }

        Console.Error.WriteLine("Direct execution not yet implemented. Use 'compile' + dotnet run.");
        return 1;
    }

    private static int RunRepl()
    {
        var repl = new Repl();
        repl.Run();
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"ZScript Compiler {Compiler.CompilerInfo.VersionString}");
        return 0;
    }

    private static int PrintUsage()
    {
        Console.WriteLine($"ZScript Compiler {Compiler.CompilerInfo.VersionString}");
        Console.WriteLine();
        Console.WriteLine("Usage: zs <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  compile <file.zs>   Compile a ZScript file");
        Console.WriteLine("  build               Build from a .zspkg package manifest");
        Console.WriteLine("  run <file.zs>       Compile and run a ZScript file");
        Console.WriteLine("  repl                Start interactive REPL");
        Console.WriteLine();
        Console.WriteLine("Options (compile):");
        Console.WriteLine("  --output, -o <path>  Output path (default: output)");
        Console.WriteLine("  --backend, -b cs|il  Backend (default: cs)");
        Console.WriteLine("  --stdlib <path>      Path to standard library modules");
        Console.WriteLine("  --ref <dir>          Directory containing CLR assemblies (repeatable)");
        Console.WriteLine();
        Console.WriteLine("Options (build):");
        Console.WriteLine("  --manifest, -m <path>  Path to .zspkg manifest (default: auto-detect)");
        Console.WriteLine("  --output, -o <path>    Output path (overrides manifest)");
        Console.WriteLine("  --backend, -b cs|il    Backend (overrides manifest)");
        Console.WriteLine("  --stdlib <path>        Stdlib path (overrides manifest)");
        Console.WriteLine("  --ref <dir>            Assembly search directory (repeatable)");
        return 0;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
