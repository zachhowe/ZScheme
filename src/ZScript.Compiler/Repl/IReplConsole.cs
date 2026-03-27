namespace ZScript.Compiler.Repl;

public interface IReplConsole
{
    string? ReadLine();
    void Write(string text);
    void WriteLine(string text);
    void WriteLine();
    void WriteErrorLine(string text);
}
