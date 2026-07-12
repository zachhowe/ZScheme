namespace ZScheme.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
}

public sealed record Diagnostic(DiagnosticSeverity Severity, string Message, SourceSpan Span)
{
    /// <summary>Stable machine-readable code (see <see cref="DiagnosticCodes" />), set only
    ///     for diagnostics that tooling keys off (e.g. LSP quick fixes). Null otherwise.</summary>
    public string? Code { get; init; }

    /// <summary>Structured payload for tooling, with a per-code convention documented on
    ///     the <see cref="DiagnosticCodes" /> constant. Null for message-only diagnostics.</summary>
    public IReadOnlyList<string>? Data { get; init; }

    public bool IsError => Severity == DiagnosticSeverity.Error;

    public override string ToString()
    {
        return $"{Severity}: {Message} at {Span}";
    }
}
