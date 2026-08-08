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
        IReadOnlyList<string>? data = null,
        IReadOnlyList<DiagnosticRelatedInfo>? related = null
    )
    {
        _diagnostics.Add(
            new Diagnostic(severity, message, span)
            {
                Code = code,
                Data = data,
                Related = related,
            }
        );
    }

    public void Error(
        string message,
        SourceSpan span,
        string? code = null,
        IReadOnlyList<string>? data = null,
        IReadOnlyList<DiagnosticRelatedInfo>? related = null
    )
    {
        Report(DiagnosticSeverity.Error, message, span, code, data, related);
    }

    public void Warning(
        string message,
        SourceSpan span,
        string? code = null,
        IReadOnlyList<string>? data = null,
        IReadOnlyList<DiagnosticRelatedInfo>? related = null
    )
    {
        Report(DiagnosticSeverity.Warning, message, span, code, data, related);
    }

    public void Hint(
        string message,
        SourceSpan span,
        string? code = null,
        IReadOnlyList<string>? data = null,
        IReadOnlyList<DiagnosticRelatedInfo>? related = null
    )
    {
        Report(DiagnosticSeverity.Hint, message, span, code, data, related);
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
