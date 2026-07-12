using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     Rename a symbol across the workspace. Reuses the same resolve → find-references
///     machinery as <see cref="ReferencesHandler" /> but emits a
///     <see cref="WorkspaceEdit" /> (every occurrence rewritten to the new name) instead
///     of a list of locations. OmniSharp 0.19 ships no <c>RenameHandlerBase</c>, so we
///     derive from the same <see cref="AbstractHandlers.Request{TParams,TResult,TRegistrationOptions,TCapability}" />
///     the framework's generated bases use and tag <see cref="IRenameHandler" /> so the
///     server routes <c>textDocument/rename</c> here.
/// </summary>
public sealed class RenameHandler(AnalysisService analysisService)
    : AbstractHandlers.Request<
        RenameParams,
        WorkspaceEdit?,
        RenameRegistrationOptions,
        RenameCapability
    >,
        IRenameHandler
{
    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new RenameRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            PrepareProvider = true,
        };
    }

    public override Task<WorkspaceEdit?> Handle(
        RenameParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<WorkspaceEdit?>(null);

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var edit = ResolveRename(
            state,
            analysisService.Index,
            line,
            col,
            request.NewName,
            request.TextDocument.Uri
        );

        return Task.FromResult(edit);
    }

    /// <summary>
    ///     Test seam: a <see cref="WorkspaceEdit" /> that rewrites every occurrence of the
    ///     symbol under the 1-based (line, col) cursor to <paramref name="newName" />,
    ///     grouped by file. Returns null when the cursor is not on a resolvable symbol.
    /// </summary>
    public static WorkspaceEdit? ResolveRename(
        DocumentState state,
        WorkspaceIndex index,
        int line,
        int col,
        string newName,
        DocumentUri fallbackUri
    )
    {
        // Locals first: scope-aware occurrences (binder + shadow-respecting uses) beat
        // the index's file-wide bare-name matching, and cover binding-site cursors
        // (let/use names, pattern variables) that have no Name node.
        if (
            state.Ast is not null
            && ScopeAnalysis.LocalOccurrences(state.Ast, line, col) is { } localOccurrences
        )
        {
            return new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                {
                    [fallbackUri] = localOccurrences
                        .Select(span => new TextEdit
                        {
                            Range = TextDocumentSyncHandler.SpanToRange(span),
                            NewText = newName,
                        })
                        .ToList(),
                },
            };
        }

        var resolved = SymbolResolver.Resolve(state, index, line, col);
        if (resolved is null)
            return null;

        var target = resolved.Value;
        var defSpan = target.DefinitionSpan;
        var references = index.FindReferences(target.QualifiedKey, target.BareName, defSpan.File);

        // Same-file occurrences bound by a shadowing local of the same name belong to
        // that local, not to the symbol being renamed.
        var locallyBound =
            state.Ast is null
                ? (IReadOnlySet<SourceSpan>)new HashSet<SourceSpan>()
                : ScopeAnalysis.OccurrencesBoundLocally(state.Ast, target.BareName);

        var byUri = new Dictionary<DocumentUri, List<TextEdit>>();
        var seen = new HashSet<(string, int, int, int)>();

        void Add(SourceSpan span)
        {
            if (locallyBound.Contains(span))
                return;
            if (!seen.Add((span.File, span.Line, span.Column, span.Length)))
                return;
            var uri = DefinitionHandler.SpanUri(span, fallbackUri);
            if (!byUri.TryGetValue(uri, out var edits))
                byUri[uri] = edits = new List<TextEdit>();
            edits.Add(
                new TextEdit
                {
                    Range = TextDocumentSyncHandler.SpanToRange(span),
                    NewText = newName,
                }
            );
        }

        foreach (var reference in references)
            Add(reference.Span);

        // Records, unions, classes and interfaces have no synthesized Name occurrence, so
        // the declaration itself must be added explicitly (mirrors ReferencesHandler).
        Add(defSpan);

        if (byUri.Count == 0)
            return null;

        return new WorkspaceEdit
        {
            Changes = byUri.ToDictionary(
                kv => kv.Key,
                kv => (IEnumerable<TextEdit>)kv.Value
            ),
        };
    }
}

/// <summary>
///     Validates a rename before the client prompts for the new name: returns the range of
///     the identifier under the cursor, or null to reject (cursor not on a renameable name).
/// </summary>
public sealed class PrepareRenameHandler(AnalysisService analysisService)
    : PrepareRenameHandlerBase
{
    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new RenameRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            PrepareProvider = true,
        };
    }

    public override Task<RangeOrPlaceholderRange?> Handle(
        PrepareRenameParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<RangeOrPlaceholderRange?>(null);

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var range = ResolvePrepareRename(state, line, col);
        // A null result means "not renameable here" — the LSP serializes it as a null reply.
        return Task.FromResult<RangeOrPlaceholderRange?>(
            range is null ? null : new RangeOrPlaceholderRange(range)
        );
    }

    /// <summary>
    ///     Test seam: the range of the renameable identifier at a 1-based (line, col), or
    ///     null if the cursor is not on a <see cref="AstNode.Name" /> node or a local
    ///     binding name (<c>let</c>/<c>use</c> names and pattern variables have no
    ///     <see cref="AstNode.Name" /> node, so those come from <see cref="ScopeAnalysis" />).
    /// </summary>
    public static Range? ResolvePrepareRename(DocumentState state, int line, int col)
    {
        if (state.Ast is null)
            return null;

        if (
            AstNavigation.FindNodeAt(state.Ast, line, col) is AstNode.Name name
            && name.Span.Length > 0
        )
            return TextDocumentSyncHandler.SpanToRange(name.Span);

        if (ScopeAnalysis.LocalOccurrences(state.Ast, line, col) is { } occurrences)
        {
            var at = occurrences.FirstOrDefault(s =>
                s.Line == line && col >= s.Column && col < s.Column + s.Length
            );
            if (at.Length > 0)
                return TextDocumentSyncHandler.SpanToRange(at);
        }

        return null;
    }
}
