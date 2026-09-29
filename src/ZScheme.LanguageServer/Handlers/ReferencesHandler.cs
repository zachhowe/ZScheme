using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

public sealed class ReferencesHandler(AnalysisService analysisService) : ReferencesHandlerBase
{
    protected override ReferenceRegistrationOptions CreateRegistrationOptions(
        ReferenceCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new ReferenceRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<LocationContainer?> Handle(
        ReferenceParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<LocationContainer?>(new LocationContainer());

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var locations = ResolveReferences(
            state,
            analysisService.Index,
            line,
            col,
            request.Context?.IncludeDeclaration ?? false,
            request.TextDocument.Uri
        );

        return Task.FromResult<LocationContainer?>(new LocationContainer(locations));
    }

    /// <summary>
    ///     Test seam: all references to the symbol under the cursor across the workspace.
    ///     Includes the declaration only when <paramref name="includeDeclaration" /> is set.
    ///     Locals are scope-aware (binder + shadow-respecting uses, mirroring
    ///     <see cref="RenameHandler.ResolveRename" />); top-level symbols exclude
    ///     same-file occurrences bound by a shadowing local of the same name. A cursor on
    ///     a type-annotation name targets the type declaration, whose reference set
    ///     includes every indexed type use.
    /// </summary>
    public static IReadOnlyList<Location> ResolveReferences(
        DocumentState state,
        WorkspaceIndex index,
        int line,
        int col,
        bool includeDeclaration,
        OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri fallbackUri
    )
    {
        var locations = new List<Location>();
        var seen = new HashSet<(string, int, int, int)>();

        void Add(SourceSpan span)
        {
            if (seen.Add((span.File, span.Line, span.Column, span.Length)))
                locations.Add(
                    new Location
                    {
                        Uri = DefinitionHandler.SpanUri(span, fallbackUri),
                        Range = TextDocumentSyncHandler.SpanToRange(span),
                    }
                );
        }

        // Locals first: scope-aware occurrences (binder + shadow-respecting uses) beat
        // the index's file-wide bare-name matching, and cover binding-site cursors
        // (let/use names, pattern variables) that have no Name node.
        if (
            state.Ast is not null
            && ScopeAnalysis.LocalOccurrences(state.Ast, line, col) is { } localOccurrences
        )
        {
            // The first occurrence is the binding site — the declaration for
            // includeDeclaration filtering.
            var binderSpan = localOccurrences[0];
            foreach (var span in localOccurrences)
                if (includeDeclaration || span != binderSpan)
                    Add(span);
            return locations;
        }

        // Type-annotation uses have no Name node, so SymbolResolver cannot reach them:
        // resolve against the recorded type uses first.
        ResolvedSymbol? maybeTarget = null;
        if (state.Ast is not null)
        {
            if (TypeNavigation.Resolve(state, index, line, col) is { } typeTarget)
                maybeTarget = typeTarget;
            else
                maybeTarget = SymbolResolver.Resolve(state, index, line, col);
        }
        if (maybeTarget is null)
            return [];

        var target = maybeTarget.Value;
        var defSpan = target.DefinitionSpan;
        // A type target's reference set is the declaration plus every indexed occurrence
        // — Name uses (constructor calls, pattern case names) and type-annotation uses
        // across files — whether the cursor started on the annotation, the declaration,
        // or a constructor call.
        var spans = TypeNavigation.IsTypeTarget(state, index, target)
            ? TypeNavigation.TypeReferenceSpans(index, target)
            : index
                .FindReferences(target.QualifiedKey, target.BareName, defSpan.File)
                .Select(r => r.Span);

        // Same-file occurrences bound by a shadowing local of the same name belong to
        // that local, not to the symbol being referenced.
        var locallyBound = state.Ast is null
            ? (IReadOnlySet<SourceSpan>)new HashSet<SourceSpan>()
            : ScopeAnalysis.OccurrencesBoundLocally(state.Ast, target.BareName);

        foreach (var span in spans)
            if (!locallyBound.Contains(span) && (includeDeclaration || span != defSpan))
                Add(span);

        // Some declarations aren't collected as a Name occurrence (records, unions,
        // classes, interfaces have no synthesized name node), so add it explicitly.
        if (includeDeclaration && !locallyBound.Contains(defSpan))
            Add(defSpan);

        return locations;
    }
}
