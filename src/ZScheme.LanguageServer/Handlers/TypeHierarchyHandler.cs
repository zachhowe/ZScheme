using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     Type hierarchy for classes and interfaces. Supertypes come from the
///     declaration's own base list (<see cref="IndexedDefinition.ImplementedInterfaces" /> —
///     the AST cannot split the base class from interfaces, so all are shown);
///     subtypes are the non-transitive read of the workspace index's implementations
///     facet, one hierarchy level per expansion. Supertype names defined in several
///     places are skipped rather than guessed.
/// </summary>
public sealed class TypeHierarchyHandler(AnalysisService analysisService)
    : TypeHierarchyHandlerBase
{
    protected override TypeHierarchyRegistrationOptions CreateRegistrationOptions(
        TypeHierarchyCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new TypeHierarchyRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<Container<TypeHierarchyItem>?> Handle(
        TypeHierarchyPrepareParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<Container<TypeHierarchyItem>?>(null);

        var item = Prepare(
            state,
            analysisService.Index,
            request.Position.Line + 1,
            request.Position.Character + 1,
            request.TextDocument.Uri
        );

        return Task.FromResult(item is null ? null : new Container<TypeHierarchyItem>(item));
    }

    public override Task<Container<TypeHierarchyItem>?> Handle(
        TypeHierarchySupertypesParams request,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<Container<TypeHierarchyItem>?>(
            new Container<TypeHierarchyItem>(Supertypes(analysisService.Index, request.Item))
        );
    }

    public override Task<Container<TypeHierarchyItem>?> Handle(
        TypeHierarchySubtypesParams request,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<Container<TypeHierarchyItem>?>(
            new Container<TypeHierarchyItem>(Subtypes(analysisService.Index, request.Item))
        );
    }

    /// <summary>Test seam: the hierarchy item for the class/interface under the 1-based
    ///     (line, col) cursor, or null when the cursor is not on one.</summary>
    public static TypeHierarchyItem? Prepare(
        DocumentState state,
        WorkspaceIndex index,
        int line,
        int col,
        DocumentUri fallbackUri
    )
    {
        var def = HierarchyItems.ResolveDefinitionAt(state, index, line, col);
        if (def is null || def.Kind is not (Analysis.SymbolKind.Class or Analysis.SymbolKind.Interface))
            return null;
        return ItemFor(def, fallbackUri);
    }

    /// <summary>Test seam: the declared base class/interfaces of <paramref name="item" />.</summary>
    public static IReadOnlyList<TypeHierarchyItem> Supertypes(
        WorkspaceIndex index,
        TypeHierarchyItem item
    )
    {
        var def = HierarchyItems.DefinitionForItem(index, item.Uri, item.SelectionRange);
        if (def?.ImplementedInterfaces is null)
            return [];

        return
        [
            .. def
                .ImplementedInterfaces.Select(index.UniqueDefinition)
                .Where(super => super is not null)
                .Select(super => ItemFor(super!, item.Uri)),
        ];
    }

    /// <summary>Test seam: the direct implementors/subclasses of <paramref name="item" />.</summary>
    public static IReadOnlyList<TypeHierarchyItem> Subtypes(
        WorkspaceIndex index,
        TypeHierarchyItem item
    )
    {
        var def = HierarchyItems.DefinitionForItem(index, item.Uri, item.SelectionRange);
        if (def is null)
            return [];

        return
        [
            .. index.DirectImplementations(def.BareName).Select(sub => ItemFor(sub, item.Uri)),
        ];
    }

    private static TypeHierarchyItem ItemFor(IndexedDefinition def, DocumentUri fallbackUri)
    {
        var range = TextDocumentSyncHandler.SpanToRange(def.Span);
        return new TypeHierarchyItem
        {
            Name = def.BareName,
            Kind = SymbolKindMapper.ToLsp(def.Kind),
            Uri = DefinitionHandler.SpanUri(def.Span, fallbackUri),
            Range = range,
            SelectionRange = range,
            Detail = def.ContainerModule,
        };
    }
}

/// <summary>Shared item↔definition plumbing for the two hierarchy handlers: a
///     hierarchy item is identified by its file + selection range, re-resolved against
///     the index on every expansion (no state is carried in <c>item.Data</c>).</summary>
internal static class HierarchyItems
{
    /// <summary>The indexed definition under the cursor, resolved like
    ///     go-to-definition (same file first, then the index).</summary>
    public static IndexedDefinition? ResolveDefinitionAt(
        DocumentState state,
        WorkspaceIndex index,
        int line,
        int col
    )
    {
        var resolved = SymbolResolver.Resolve(state, index, line, col);
        if (resolved is null)
            return null;

        var target = resolved.Value;
        var candidates = index.ResolveDefinition(target.QualifiedKey, target.BareName);
        return candidates.FirstOrDefault(d => d.Span == target.DefinitionSpan)
            ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    /// <summary>Maps a hierarchy item (from a previous prepare/expansion) back to its
    ///     indexed definition via its file and selection range.</summary>
    public static IndexedDefinition? DefinitionForItem(
        WorkspaceIndex index,
        OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri uri,
        OmniSharp.Extensions.LanguageServer.Protocol.Models.Range selectionRange
    )
    {
        string file;
        try
        {
            file = Path.GetFullPath(uri.GetFileSystemPath());
        }
        catch
        {
            return null;
        }

        return index
            .DefinitionsInFile(file)
            .FirstOrDefault(d =>
                TextDocumentSyncHandler.SpanToRange(d.Span).Start == selectionRange.Start
            );
    }
}
