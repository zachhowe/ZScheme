namespace ZScheme.Compiler.Diagnostics;

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
    public bool HasErrors => _diagnostics.Any(d => d.IsError);

    public void Report(
        DiagnosticSeverity severity,
        string message,
        SourceSpan span,
        string? code = null,
        IReadOnlyList<string>? data = null
    )
    {
        _diagnostics.Add(new Diagnostic(severity, message, span) { Code = code, Data = data });
    }

    public void Error(
        string message,
        SourceSpan span,
        string? code = null,
        IReadOnlyList<string>? data = null
    )
    {
        Report(DiagnosticSeverity.Error, message, span, code, data);
    }

    public void Warning(
        string message,
        SourceSpan span,
        string? code = null,
        IReadOnlyList<string>? data = null
    )
    {
        Report(DiagnosticSeverity.Warning, message, span, code, data);
    }

    public void Clear()
    {
        _diagnostics.Clear();
    }

    public void AddRange(DiagnosticBag other)
    {
        _diagnostics.AddRange(other._diagnostics);
    }
}
