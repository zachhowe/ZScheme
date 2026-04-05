namespace ZScheme.Compiler.Diagnostics;

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
    public bool HasErrors => _diagnostics.Any(d => d.IsError);

    public void Report(DiagnosticSeverity severity, string message, SourceSpan span)
    {
        _diagnostics.Add(new Diagnostic(severity, message, span));
    }

    public void Error(string message, SourceSpan span)
    {
        Report(DiagnosticSeverity.Error, message, span);
    }

    public void Warning(string message, SourceSpan span)
    {
        Report(DiagnosticSeverity.Warning, message, span);
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
