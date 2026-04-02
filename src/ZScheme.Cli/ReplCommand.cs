using ZScheme.Compiler.Repl;

namespace ZScheme.Cli;

internal static class ReplCommand
{
    public static int Run()
    {
        var repl = new Repl();
        repl.Run();
        return 0;
    }
}
