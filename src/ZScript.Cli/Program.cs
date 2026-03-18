namespace ZScript.Cli;

using ZScript.Compiler.Pipeline;

public class Program
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
            "run" => RunExecute(args[1..]),
            "repl" => RunRepl(),
            "--help" or "-h" => PrintUsage(),
            _ => Error($"Unknown command: {command}")
        };
    }

    private static int RunCompile(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: zs compile <file.zs> [--output <path>] [--backend cs|il] [--stdlib <path>]");
            return 1;
        }

        var filePath = args[0];
        var outputPath = "output";
        var backend = OutputMode.CSharp;
        string? stdlibPath = null;

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
            StdLibPath = stdlibPath
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
            var outputFile = Path.ChangeExtension(outputPath, ".dll");
            // IL output would be byte[] — write to file
            Console.WriteLine($"Generated: {outputFile}");
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

    private static int PrintUsage()
    {
        Console.WriteLine("ZScript Compiler");
        Console.WriteLine();
        Console.WriteLine("Usage: zs <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  compile <file.zs>   Compile a ZScript file");
        Console.WriteLine("  run <file.zs>       Compile and run a ZScript file");
        Console.WriteLine("  repl                Start interactive REPL");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --output, -o <path>  Output path (default: output)");
        Console.WriteLine("  --backend, -b cs|il  Backend (default: cs)");
        Console.WriteLine("  --stdlib <path>      Path to standard library modules");
        return 0;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
