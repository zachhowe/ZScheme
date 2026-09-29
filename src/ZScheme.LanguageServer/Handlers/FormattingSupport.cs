using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;
using Fmt = ZScheme.Formatter.Formatter;
using FmtOptions = ZScheme.Formatter.FormattingOptions;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>Shared body of <see cref="DocumentFormattingHandler" /> and
///     <see cref="DocumentRangeFormattingHandler" />: both are the same full-document format,
///     differing only in whether the resulting edits are filtered to a selection.</summary>
internal static class FormattingSupport
{
    public static readonly TextDocumentSelector Selector = new(
        TextDocumentFilter.ForLanguage("zscheme"),
        TextDocumentFilter.ForPattern("**/*.zs")
    );

    public static IReadOnlyList<TextEdit> ComputeEdits(
        AnalysisService analysisService,
        DocumentUri uri,
        FormattingOptions? clientOptions,
        Range? range = null
    )
    {
        string path;
        try
        {
            path = uri.GetFileSystemPath();
        }
        catch
        {
            // Non-file URIs have no directory to resolve .zsfmt/.editorconfig against.
            return [];
        }

        // The language filter in the document selector can also match a .zspkg manifest, which
        // is a different grammar than the formatter's special-form and import-merge tables target.
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".zs", StringComparison.OrdinalIgnoreCase))
            return [];

        var key = uri.ToString();
        var source = analysisService.GetBufferText(key) ?? analysisService.GetDocument(key)?.Source;
        if (source is null)
            return [];

        var options = Fmt.ResolveOptions(path, ClientBase(clientOptions));
        var result = Fmt.FormatSource(source, path, options);

        // A non-null warning means the formatter declined — a lex/parse error, or its re-lex
        // safety guard tripping. Stay silent rather than surfacing an error: a format keystroke
        // while the file is mid-edit is routine, and the diagnostics already say what is wrong.
        if (result.Warning is not null || !result.Changed)
            return [];

        return FormattingEdits.Compute(source, result.Formatted, range);
    }

    /// <summary>
    ///     The client's indentation settings as the <em>base</em> the project config is layered
    ///     onto, giving a precedence of defaults &lt; client &lt; <c>.editorconfig</c> &lt;
    ///     <c>.zsfmt</c>. An editor's own indent setting therefore applies only where the project
    ///     has not pinned one, and a repo with a <c>.zsfmt</c> formats identically for everyone.
    ///     <para>
    ///         <c>tabSize</c>/<c>insertSpaces</c> are required by the protocol, but a client that
    ///         sends neither would arrive here as <c>tabSize: 0</c>, so both are applied together
    ///         only when the size is usable.
    ///     </para>
    /// </summary>
    private static FmtOptions ClientBase(FormattingOptions? clientOptions)
    {
        if (clientOptions is not { TabSize: > 0 } options)
            return FmtOptions.Default;

        return FmtOptions.Default with
        {
            IndentSize = (int)options.TabSize,
            UseTabs = !options.InsertSpaces,
        };
    }
}
