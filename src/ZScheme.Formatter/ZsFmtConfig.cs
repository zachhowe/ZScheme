using System.Text;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Formatter;

/// <summary>
/// Loads per-directory formatter configuration from <c>.zsfmt</c> dot files. A <c>.zsfmt</c> is an
/// S-expression file holding a single <c>(format ...)</c> form whose clauses override individual
/// <see cref="FormattingOptions"/> fields. Resolution walks up from the file being formatted; a more deeply
/// nested <c>.zsfmt</c> overrides settings inherited from above (nearest wins), and a <c>(root #t)</c> clause
/// stops the upward search. Unknown clauses, malformed values, and unparseable files are ignored rather than
/// treated as errors, mirroring how <see cref="EditorConfigParser"/> tolerates unrecognized keys.
/// </summary>
public static class ZsFmtConfig
{
    public const string FileName = ".zsfmt";

    /// <summary>
    /// Overlays the nearest-wins stack of <c>.zsfmt</c> files found above <paramref name="filePath"/> onto
    /// <paramref name="baseOptions"/>. Returns <paramref name="baseOptions"/> unchanged when none are found.
    /// </summary>
    public static FormattingOptions Resolve(string filePath, FormattingOptions baseOptions)
    {
        var directory =
            Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();

        // Collect overrides nearest-first, stopping once a file declares itself the root.
        var overrides = new List<ZsFmtOverrides>();
        for (var dir = directory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            var configPath = Path.Combine(dir, FileName);
            if (!File.Exists(configPath))
                continue;

            var parsed = ParseFile(configPath);
            if (parsed == null)
                continue;

            overrides.Add(parsed);
            if (parsed.IsRoot)
                break;
        }

        // Apply farthest-first so the nearest file's settings land last and win.
        var result = baseOptions;
        for (var i = overrides.Count - 1; i >= 0; i--)
            result = overrides[i].Apply(result);

        return result;
    }

    /// <summary>
    /// Renders a complete <c>.zsfmt</c> file spelling out every option at its built-in default, suitable for
    /// scaffolding a starter config (<c>zs format --init</c>). The output round-trips: feeding it back through
    /// <see cref="Resolve"/> yields <see cref="FormattingOptions.Default"/>. The two keyword-set clauses list
    /// the default members; because the loader treats them as additive deltas, re-listing the defaults is a
    /// no-op the user can edit — prefix a name with <c>-</c> to drop a default.
    /// </summary>
    public static string RenderDefault() => RenderDefault(FormattingOptions.Default);

    /// <inheritdoc cref="RenderDefault()"/>
    public static string RenderDefault(FormattingOptions options)
    {
        static string Bool(bool value) => value ? "#t" : "#f";
        static string Keywords(IReadOnlySet<string> set) =>
            string.Join(' ', set.OrderBy(s => s, StringComparer.Ordinal));

        var sb = new StringBuilder();
        sb.Append("; .zsfmt — ZScheme formatter configuration for this directory subtree.\n");
        sb.Append(
            "; Resolution walks up from the file being formatted; a nested .zsfmt overrides\n"
        );
        sb.Append(
            "; settings inherited from above (nearest wins). A (root #t) clause stops the walk.\n"
        );
        sb.Append(
            "; Every value below is the built-in default — edit to taste, or delete lines to inherit.\n"
        );
        sb.Append("(format\n");
        sb.Append("  ; Stop the upward search at this directory.\n");
        sb.Append("  (root #f)\n\n");
        sb.Append("  ; Spaces per indent level (ignored when indent-style is tab).\n");
        sb.Append($"  (indent-size {options.IndentSize})\n\n");
        sb.Append("  ; Indentation character: space or tab.\n");
        sb.Append($"  (indent-style {(options.UseTabs ? "tab" : "space")})\n\n");
        sb.Append(
            "  ; Line-width budget deciding whether a form is kept flat or broken across lines.\n"
        );
        sb.Append($"  (max-line-length {options.MaxLineLength})\n\n");
        sb.Append("  ; Ensure the file ends with a single trailing newline.\n");
        sb.Append($"  (insert-final-newline {Bool(options.InsertFinalNewline)})\n\n");
        sb.Append("  ; Strip trailing whitespace from every line.\n");
        sb.Append($"  (trim-trailing-whitespace {Bool(options.TrimTrailingWhitespace)})\n\n");
        sb.Append("  ; Merge consecutive top-level (import ...) forms into one.\n");
        sb.Append($"  (merge-imports {Bool(options.MergeImports)})\n\n");
        sb.Append("  ; Spaces inserted before a trailing same-line comment.\n");
        sb.Append($"  (trailing-comment-spaces {options.TrailingCommentSpaces})\n\n");
        sb.Append(
            "  ; The two keyword sets below list the built-in defaults. Names are ADDED to those\n"
        );
        sb.Append("  ; defaults; prefix a name with '-' to drop a default (e.g. -if).\n");
        sb.Append("  ; Forms whose first operand stays on the head line when the form breaks:\n");
        sb.Append($"  (keep-first-operand {Keywords(options.KeepFirstOperand)})\n");
        sb.Append("  ; Block forms whose body/clauses are always stacked one per line:\n");
        sb.Append($"  (always-break-body {Keywords(options.AlwaysBreakBody)}))\n");
        return sb.ToString();
    }

    private static ZsFmtOverrides? ParseFile(string path)
    {
        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(source, path, diagnostics).Tokenize();
        if (diagnostics.HasErrors)
            return null;

        var forms = new SExprParser(tokens, diagnostics).ParseAll();
        if (diagnostics.HasErrors)
            return null;

        var formatForm = forms
            .OfType<SExpr.SList>()
            .FirstOrDefault(l => l.Items is [SExpr.Atom { Text: "format" }, ..]);
        if (formatForm == null)
            return null;

        var isRoot = false;
        int? indentSize = null;
        bool? useTabs = null;
        bool? insertFinalNewline = null;
        bool? trimTrailingWhitespace = null;
        int? maxLineLength = null;
        bool? mergeImports = null;
        int? trailingCommentSpaces = null;
        var keepDelta = new List<(string Name, bool Remove)>();
        var breakDelta = new List<(string Name, bool Remove)>();

        for (var i = 1; i < formatForm.Items.Count; i++)
        {
            if (formatForm.Items[i] is not SExpr.SList { Items: [SExpr.Atom keyAtom, ..] } clause)
                continue;

            var values = clause.Items.Skip(1).ToList();

            switch (keyAtom.Text)
            {
                case "root" when ParseBool(values) is { } root:
                    isRoot = root;
                    break;
                case "indent-size" when ParseInt(values) is { } size && size > 0:
                    indentSize = size;
                    break;
                case "indent-style" when ParseIndentStyle(values) is { } tabs:
                    useTabs = tabs;
                    break;
                case "max-line-length" when ParseInt(values) is { } len && len > 0:
                    maxLineLength = len;
                    break;
                case "insert-final-newline" when ParseBool(values) is { } newline:
                    insertFinalNewline = newline;
                    break;
                case "trim-trailing-whitespace" when ParseBool(values) is { } trim:
                    trimTrailingWhitespace = trim;
                    break;
                case "merge-imports" when ParseBool(values) is { } merge:
                    mergeImports = merge;
                    break;
                case "trailing-comment-spaces" when ParseInt(values) is { } spaces && spaces >= 0:
                    trailingCommentSpaces = spaces;
                    break;
                case "keep-first-operand":
                    keepDelta.AddRange(ParseDelta(values));
                    break;
                case "always-break-body":
                    breakDelta.AddRange(ParseDelta(values));
                    break;
                // Unknown or malformed clauses are ignored.
            }
        }

        return new ZsFmtOverrides
        {
            IsRoot = isRoot,
            IndentSize = indentSize,
            UseTabs = useTabs,
            InsertFinalNewline = insertFinalNewline,
            TrimTrailingWhitespace = trimTrailingWhitespace,
            MaxLineLength = maxLineLength,
            MergeImports = mergeImports,
            TrailingCommentSpaces = trailingCommentSpaces,
            KeepFirstOperandDelta = keepDelta,
            AlwaysBreakBodyDelta = breakDelta,
        };
    }

    // Accepts the boolean literals #t/#f as well as the symbols true/false (case-insensitive).
    private static bool? ParseBool(IReadOnlyList<SExpr> values)
    {
        if (values is not [SExpr.Atom atom])
            return null;

        return atom.Text.ToLowerInvariant() switch
        {
            "#t" or "true" => true,
            "#f" or "false" => false,
            _ => null,
        };
    }

    private static int? ParseInt(IReadOnlyList<SExpr> values)
    {
        if (
            values is [SExpr.Atom { Kind: TokenKind.IntLit } atom]
            && int.TryParse(atom.Text, out var value)
        )
            return value;

        return null;
    }

    private static bool? ParseIndentStyle(IReadOnlyList<SExpr> values)
    {
        if (values is not [SExpr.Atom atom])
            return null;

        return atom.Text.ToLowerInvariant() switch
        {
            "tab" => true,
            "space" => false,
            _ => null,
        };
    }

    // Each value is a keyword symbol; a leading '-' removes it from the inherited set, otherwise it is added.
    private static List<(string Name, bool Remove)> ParseDelta(IReadOnlyList<SExpr> values)
    {
        var result = new List<(string Name, bool Remove)>();
        foreach (var value in values)
        {
            if (value is not SExpr.Atom atom || atom.Text.Length == 0)
                continue;

            if (atom.Text[0] == '-' && atom.Text.Length > 1)
                result.Add((atom.Text[1..], true));
            else
                result.Add((atom.Text, false));
        }

        return result;
    }

    private sealed record ZsFmtOverrides
    {
        public bool IsRoot { get; init; }
        public int? IndentSize { get; init; }
        public bool? UseTabs { get; init; }
        public bool? InsertFinalNewline { get; init; }
        public bool? TrimTrailingWhitespace { get; init; }
        public int? MaxLineLength { get; init; }
        public bool? MergeImports { get; init; }
        public int? TrailingCommentSpaces { get; init; }
        public IReadOnlyList<(string Name, bool Remove)> KeepFirstOperandDelta { get; init; } = [];
        public IReadOnlyList<(string Name, bool Remove)> AlwaysBreakBodyDelta { get; init; } = [];

        public FormattingOptions Apply(FormattingOptions options)
        {
            if (IndentSize is { } indentSize)
                options = options with { IndentSize = indentSize };
            if (UseTabs is { } useTabs)
                options = options with { UseTabs = useTabs };
            if (InsertFinalNewline is { } insertFinalNewline)
                options = options with { InsertFinalNewline = insertFinalNewline };
            if (TrimTrailingWhitespace is { } trimTrailingWhitespace)
                options = options with { TrimTrailingWhitespace = trimTrailingWhitespace };
            if (MaxLineLength is { } maxLineLength)
                options = options with { MaxLineLength = maxLineLength };
            if (MergeImports is { } mergeImports)
                options = options with { MergeImports = mergeImports };
            if (TrailingCommentSpaces is { } trailingCommentSpaces)
                options = options with { TrailingCommentSpaces = trailingCommentSpaces };
            if (KeepFirstOperandDelta.Count > 0)
                options = options with
                {
                    KeepFirstOperand = ApplyDelta(options.KeepFirstOperand, KeepFirstOperandDelta),
                };
            if (AlwaysBreakBodyDelta.Count > 0)
                options = options with
                {
                    AlwaysBreakBody = ApplyDelta(options.AlwaysBreakBody, AlwaysBreakBodyDelta),
                };

            return options;
        }

        private static IReadOnlySet<string> ApplyDelta(
            IReadOnlySet<string> baseSet,
            IReadOnlyList<(string Name, bool Remove)> delta
        )
        {
            var set = new HashSet<string>(baseSet);
            foreach (var (name, remove) in delta)
            {
                if (remove)
                    set.Remove(name);
                else
                    set.Add(name);
            }

            return set;
        }
    }
}
