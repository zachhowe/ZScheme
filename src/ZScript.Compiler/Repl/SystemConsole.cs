namespace ZScript.Compiler.Repl;

public sealed class SystemConsole : IReplConsole
{
    public string? ReadLine() => Console.ReadLine();

    public void Write(string text) => Console.Write(text);

    public void WriteLine(string text) => Console.WriteLine(text);

    public void WriteLine() => Console.WriteLine();

    public void WriteErrorLine(string text) => Console.Error.WriteLine(text);
}
