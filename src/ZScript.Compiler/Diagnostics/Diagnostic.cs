namespace ZScript.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info
}

public sealed record Diagnostic(DiagnosticSeverity Severity, string Message, SourceSpan Span)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;

    public override string ToString() =>
        $"{Severity}: {Message} at {Span}";
}
