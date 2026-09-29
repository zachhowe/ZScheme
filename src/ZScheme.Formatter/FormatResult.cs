namespace ZScheme.Formatter;

/// <summary>
/// Result of formatting a file. When <see cref="Warning"/> is non-null the formatter declined to
/// rewrite the file (e.g. the lexer reported errors, or the re-lex safety guard detected that
/// formatting would have altered the code token stream); <see cref="Formatted"/> then holds the
/// original source unchanged.
/// </summary>
public sealed record FormatResult(string Formatted, bool Changed, string? Warning = null);
