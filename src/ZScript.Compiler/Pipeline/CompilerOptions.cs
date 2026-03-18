namespace ZScript.Compiler.Pipeline;

public enum OutputMode
{
    CSharp,
    IL
}

public sealed class CompilerOptions
{
    public OutputMode OutputMode { get; set; } = OutputMode.CSharp;
    public string OutputPath { get; set; } = "output";
    public string Namespace { get; set; } = "ZScriptGenerated";
    public bool EmitDebugInfo { get; set; }
    public string? StdLibPath { get; set; }
}
