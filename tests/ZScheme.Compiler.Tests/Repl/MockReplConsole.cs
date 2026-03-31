using ZScheme.Compiler.Repl;

namespace ZScheme.Compiler.Tests.Repl;

public sealed class MockReplConsole : IReplConsole
{
    public Queue<string?> Inputs { get; } = new();
    public List<string> WrittenText { get; } = [];
    public List<string> WrittenLines { get; } = [];
    public List<string> ErrorLines { get; } = [];

    public string? ReadLine()
    {
        return Inputs.Count > 0 ? Inputs.Dequeue() : null;
    }

    public void Write(string text)
    {
        WrittenText.Add(text);
    }

    public void WriteLine(string text)
    {
        WrittenLines.Add(text);
    }

    public void WriteLine()
    {
        WrittenLines.Add("");
    }

    public void WriteErrorLine(string text)
    {
        ErrorLines.Add(text);
    }

    public void ClearTracking()
    {
        Inputs.Clear();
        WrittenText.Clear();
        WrittenLines.Clear();
        ErrorLines.Clear();
    }
}
