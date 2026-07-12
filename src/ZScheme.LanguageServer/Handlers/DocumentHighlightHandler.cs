using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     Highlights every occurrence of the symbol under the cursor within the current file.
///     Uses the same resolve → find-references data as <see cref="ReferencesHandler" />,
///     filtered to the active document.
/// </summary>
public sealed class DocumentHighlightHandler(AnalysisService analysisService)
    : DocumentHighlightHandlerBase
{
    protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(
        DocumentHighlightCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DocumentHighlightRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<DocumentHighlightContainer?> Handle(
        DocumentHighlightParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<DocumentHighlightContainer?>(null);

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var highlights = ResolveHighlights(
            state,
            analysisService.Index,
            line,
            col,
            request.TextDocument.Uri.GetFileSystemPath()
        );

        return Task.FromResult<DocumentHighlightContainer?>(
            new DocumentHighlightContainer(highlights)
        );
    }

    /// <summary>
    ///     Test seam: occurrences of the symbol under the 1-based (line, col) cursor that
    ///     live in <paramref name="currentFilePath" />. Cross-file references are excluded
    ///     because document highlight is single-file.
    /// </summary>
    public static IReadOnlyList<DocumentHighlight> ResolveHighlights(
        DocumentState state,
        WorkspaceIndex index,
        int line,
        int col,
        string currentFilePath
    )
    {
        var resolved = SymbolResolver.ResolveIncludingLocals(state, index, line, col);
        if (resolved is null)
            return [];

        var target = resolved.Value;
        var references = index.FindReferences(
            target.QualifiedKey,
            target.BareName,
            target.DefinitionSpan.File
        );

        var highlights = new List<DocumentHighlight>();
        var seen = new HashSet<(int, int, int)>();

        void Add(SourceSpan span)
        {
            if (!string.Equals(span.File, currentFilePath, StringComparison.OrdinalIgnoreCase))
                return;
            if (!seen.Add((span.Line, span.Column, span.Length)))
                return;
            highlights.Add(
                new DocumentHighlight
                {
                    Range = TextDocumentSyncHandler.SpanToRange(span),
                    Kind = DocumentHighlightKind.Text,
                }
            );
        }

        foreach (var reference in references)
            Add(reference.Span);

        // Declarations without a synthesized Name occurrence (records/unions/classes/
        // interfaces) still get highlighted when the definition is in this file.
        Add(target.DefinitionSpan);

        return highlights;
    }
}
