using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Formatter;

public static class Formatter
{
    public static FormatResult FormatFile(string filePath, FormattingOptions? options = null)
    {
        return FormatSource(File.ReadAllText(filePath), filePath, options);
    }

    /// <summary>
    /// Formats <paramref name="source" /> as if it were the contents of <paramref name="filePath" />.
    /// The path is still required: it anchors the <c>.editorconfig</c>/<c>.zsfmt</c> search and names the
    /// file in lexer diagnostics. Used by the language server, whose text is the editor buffer rather
    /// than what is on disk.
    /// </summary>
    public static FormatResult FormatSource(
        string source,
        string filePath,
        FormattingOptions? options = null
    )
    {
        options ??= ResolveOptions(filePath);

        var diagnostics = new DiagnosticBag();
        var lexer = new Lexer(source, filePath, diagnostics);
        var (tokens, comments) = lexer.TokenizeWithComments();

        if (diagnostics.HasErrors)
            return new FormatResult(source, false, "Skipped: source has lexer errors.");

        var parser = new SExprParser(tokens, diagnostics);
        var sExprs = parser.ParseAll();

        if (diagnostics.HasErrors)
            return new FormatResult(source, false, "Skipped: source has parse errors.");

        if (options.MergeImports)
            sExprs = ImportMerger.MergeImports(sExprs);

        var commentMap = CommentAttacher.Attach(sExprs, comments, tokens);

        var formatted = PrettyPrinter.Format(sExprs, options, commentMap);
        formatted = NormalizeLineEndings(formatted);

        // Safety guard: the formatted output must lex to exactly the same code tokens as the tree we
        // intended to print. We compare against a canonical single-line rendering of that tree (not the
        // original source) so the deliberate import-merge is allowed, while any layout bug that drops,
        // reorders, or rewrites a token is caught — in which case we refuse to rewrite and return the
        // original untouched. Comments are excluded since their positions may legitimately move.
        var intended = string.Join("\n", sExprs.Select(PrettyPrinter.Flat));
        if (!CodeTokensEqual(filePath, intended, formatted))
        {
            return new FormatResult(
                source,
                false,
                "Skipped: formatting would have changed the code token stream (internal formatter bug). File left unchanged."
            );
        }

        var normalizedSource = NormalizeLineEndings(source);
        var changed = formatted != normalizedSource;

        return new FormatResult(formatted, changed);
    }

    /// <summary>
    /// Resolves the options that apply to <paramref name="filePath" />. Resolution order
    /// (low → high precedence): <paramref name="baseOptions" /> (built-in defaults when omitted)
    /// &lt; <c>.editorconfig</c> &lt; <c>.zsfmt</c>, with nearer <c>.zsfmt</c> files overriding ones
    /// further up the tree. The language server passes the client's <c>tabSize</c>/<c>insertSpaces</c>
    /// as <paramref name="baseOptions" />, so editor settings apply only where the project has not
    /// pinned the value.
    /// </summary>
    public static FormattingOptions ResolveOptions(
        string filePath,
        FormattingOptions? baseOptions = null
    )
    {
        baseOptions ??= FormattingOptions.Default;
        return ZsFmtConfig.Resolve(filePath, EditorConfigParser.TryParse(filePath, baseOptions));
    }

    private static bool CodeTokensEqual(string filePath, string original, string formatted)
    {
        var before = CodeTokens(original, filePath);
        var after = CodeTokens(formatted, filePath);

        if (before.Count != after.Count)
            return false;

        for (var i = 0; i < before.Count; i++)
            if (before[i].Kind != after[i].Kind || before[i].Text != after[i].Text)
                return false;

        return true;
    }

    private static List<Token> CodeTokens(string text, string filePath)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, filePath, diagnostics).Tokenize();
        // If the formatted text fails to lex cleanly the guard treats it as a mismatch, since an
        // empty/short token stream will not equal the original.
        return diagnostics.HasErrors ? [] : tokens;
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n");
    }
}
