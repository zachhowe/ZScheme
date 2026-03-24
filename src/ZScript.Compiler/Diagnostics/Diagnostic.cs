namespace ZScript.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning
}

public sealed record Diagnostic(DiagnosticSeverity Severity, string Message, SourceSpan Span)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;

    public override string ToString()
    {
        return $"{Severity}: {Message} at {Span}";
    }
}
