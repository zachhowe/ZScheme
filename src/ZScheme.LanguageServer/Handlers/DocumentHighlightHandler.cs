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

        // Locals first: scope-aware occurrences beat the index's file-wide bare-name
        // matching, and cover binding-site cursors that have no Name node.
        if (
            state.Ast is not null
            && ScopeAnalysis.LocalOccurrences(state.Ast, line, col) is { } localOccurrences
        )
        {
            foreach (var span in localOccurrences)
                Add(span);
            return highlights;
        }

        var resolved = SymbolResolver.Resolve(state, index, line, col);
        if (resolved is null)
            return [];

        var target = resolved.Value;
        var references = index.FindReferences(
            target.QualifiedKey,
            target.BareName,
            target.DefinitionSpan.File
        );

        // Occurrences bound by a shadowing local of the same name belong to that local.
        var locallyBound =
            state.Ast is null
                ? (IReadOnlySet<SourceSpan>)new HashSet<SourceSpan>()
                : ScopeAnalysis.OccurrencesBoundLocally(state.Ast, target.BareName);

        foreach (var reference in references)
            if (!locallyBound.Contains(reference.Span))
                Add(reference.Span);

        // Declarations without a synthesized Name occurrence (records/unions/classes/
        // interfaces) still get highlighted when the definition is in this file.
        if (!locallyBound.Contains(target.DefinitionSpan))
            Add(target.DefinitionSpan);

        return highlights;
    }
}
