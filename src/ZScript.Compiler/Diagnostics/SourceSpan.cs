namespace ZScript.Compiler.Diagnostics;

public readonly record struct SourceSpan(string File, int Line, int Column, int Length)
{
    public static readonly SourceSpan None = new("", 0, 0, 0);

    public override string ToString()
    {
        return string.IsNullOrEmpty(File) ? $"({Line}:{Column})" : $"{File}({Line}:{Column})";
    }
}
