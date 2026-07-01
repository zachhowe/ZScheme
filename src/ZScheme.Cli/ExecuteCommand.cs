namespace ZScheme.Cli;

internal static class ExecuteCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: zs run <file.zs>");
            return 1;
        }

        Console.Error.WriteLine(
            "Direct execution not yet implemented. Use 'compile' + dotnet run."
        );
        return 1;
    }
}
