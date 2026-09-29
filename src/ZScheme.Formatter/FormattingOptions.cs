namespace ZScheme.Formatter;

public sealed record FormattingOptions
{
    public int IndentSize { get; init; } = 4;
    public bool UseTabs { get; init; } = false;
    public bool InsertFinalNewline { get; init; } = true;
    public bool TrimTrailingWhitespace { get; init; } = true;

    // The line-width budget that decides whether a form is kept flat or broken across lines.
    public int MaxLineLength { get; init; } = 100;

    // Whether consecutive top-level (import ...) forms are merged into a single import.
    public bool MergeImports { get; init; } = true;

    // Number of spaces inserted before a trailing same-line comment.
    public int TrailingCommentSpaces { get; init; } = 2;

    // Special forms whose first operand stays on the head line when the form breaks; and block forms
    // whose body/clauses are always stacked one-per-line. Sourced here so they double as the loader's
    // starting point — a .zsfmt may add to or remove from these inherited sets.
    public IReadOnlySet<string> KeepFirstOperand { get; init; } = DefaultKeepFirstOperand;
    public IReadOnlySet<string> AlwaysBreakBody { get; init; } = DefaultAlwaysBreakBody;

    public string IndentString => UseTabs ? "\t" : new(' ', IndentSize);

    public static FormattingOptions Default => new();

    public static readonly IReadOnlySet<string> DefaultKeepFirstOperand = new HashSet<string>
    {
        "if",
        "when",
        "unless",
        "match",
        "lambda",
        "with",
        "set!",
        "define-union",
        "define-record",
        "define-struct",
        "define-interface",
        "define-class",
        "module",
        "import-clr",
    };

    public static readonly IReadOnlySet<string> DefaultAlwaysBreakBody = new HashSet<string>
    {
        "match",
        "cond",
        "begin",
        "when",
        "unless",
        "with",
        "define-union",
        "define-class",
        "define-interface",
        "object",
    };
}
