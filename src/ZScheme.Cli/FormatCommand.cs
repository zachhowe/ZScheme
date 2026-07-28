using ZScheme.Formatter;
using Fmt = ZScheme.Formatter.Formatter;

namespace ZScheme.Cli;

internal static class FormatCommand
{
    public static int Run(string[] args)
    {
        if (args.Contains("--init") || (args.Length > 0 && args[0] == "init"))
            return RunInit(args);

        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: zs format <file.zs> [--write]");
            Console.Error.WriteLine(
                "       zs format --init [--force]   Write a default .zsfmt to the current directory"
            );
            return 1;
        }

        var filePath = args[0];
        var write = args.Contains("--write");

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            return 1;
        }

        var result = Fmt.FormatFile(filePath);

        if (result.Warning != null)
        {
            Console.Error.WriteLine($"{filePath}: {result.Warning}");
            if (!write)
                Console.Write(result.Formatted);
            return 1;
        }

        if (write)
        {
            if (result.Changed)
            {
                File.WriteAllText(filePath, result.Formatted);
                Console.WriteLine($"Formatted: {filePath}");
            }
            else
            {
                Console.WriteLine($"No changes: {filePath}");
            }
        }
        else
        {
            Console.Write(result.Formatted);
        }

        return 0;
    }

    private static int RunInit(string[] args)
    {
        var force = args.Contains("--force") || args.Contains("-f");
        var path = Path.Combine(Directory.GetCurrentDirectory(), ZsFmtConfig.FileName);

        if (File.Exists(path) && !force)
        {
            Console.Error.WriteLine($"{ZsFmtConfig.FileName} already exists: {path}");
            Console.Error.WriteLine("Pass --force to overwrite.");
            return 1;
        }

        File.WriteAllText(path, ZsFmtConfig.RenderDefault());
        Console.WriteLine($"Wrote {path}");
        return 0;
    }
}
