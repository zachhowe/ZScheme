using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     Call hierarchy (peek callers/callees). The compiler records no call graph, so
///     both directions are derived from the workspace index: each reference is tagged
///     with its enclosing top-level definition (<see cref="IndexedReference.ContainingDefinition" />),
///     incoming calls group a function's references by that container, and outgoing
///     calls resolve the references contained in the function to their definitions.
///     Module-scope calls (no container) don't appear as callers, and calls into
///     never-opened unindexed files share find-references' staleness limits.
/// </summary>
public sealed class CallHierarchyHandler(AnalysisService analysisService)
    : CallHierarchyHandlerBase
{
    protected override CallHierarchyRegistrationOptions CreateRegistrationOptions(
        CallHierarchyCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new CallHierarchyRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<Container<CallHierarchyItem>?> Handle(
        CallHierarchyPrepareParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<Container<CallHierarchyItem>?>(null);

        var item = Prepare(
            state,
            analysisService.Index,
            request.Position.Line + 1,
            request.Position.Character + 1,
            request.TextDocument.Uri
        );

        return Task.FromResult(item is null ? null : new Container<CallHierarchyItem>(item));
    }

    public override Task<Container<CallHierarchyIncomingCall>?> Handle(
        CallHierarchyIncomingCallsParams request,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<Container<CallHierarchyIncomingCall>?>(
            new Container<CallHierarchyIncomingCall>(Incoming(analysisService.Index, request.Item))
        );
    }

    public override Task<Container<CallHierarchyOutgoingCall>?> Handle(
        CallHierarchyOutgoingCallsParams request,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(
            new Container<CallHierarchyOutgoingCall>(Outgoing(analysisService.Index, request.Item))
        );
    }

    /// <summary>Test seam: the hierarchy item for the function under the 1-based
    ///     (line, col) cursor, or null when the cursor is not on a function.</summary>
    public static CallHierarchyItem? Prepare(
        DocumentState state,
        WorkspaceIndex index,
        int line,
        int col,
        DocumentUri fallbackUri
    )
    {
        var def = HierarchyItems.ResolveDefinitionAt(state, index, line, col);
        if (def is null || def.Kind != Analysis.SymbolKind.Function)
            return null;
        return ItemFor(def, fallbackUri);
    }

    /// <summary>Test seam: callers of the function <paramref name="item" /> represents.</summary>
    public static IReadOnlyList<CallHierarchyIncomingCall> Incoming(
        WorkspaceIndex index,
        CallHierarchyItem item
    )
    {
        var def = HierarchyItems.DefinitionForItem(index, item.Uri, item.SelectionRange);
        if (def is null)
            return [];

        return
        [
            .. index
                .IncomingCalls(def.QualifiedKey, def.BareName, def.File, def.Span)
                .Select(call => new CallHierarchyIncomingCall
                {
                    From = ItemFor(call.Caller, item.Uri),
                    FromRanges = new Container<
                        OmniSharp.Extensions.LanguageServer.Protocol.Models.Range
                    >(call.FromSpans.Select(TextDocumentSyncHandler.SpanToRange)),
                }),
        ];
    }

    /// <summary>Test seam: callees of the function <paramref name="item" /> represents.</summary>
    public static IReadOnlyList<CallHierarchyOutgoingCall> Outgoing(
        WorkspaceIndex index,
        CallHierarchyItem item
    )
    {
        var def = HierarchyItems.DefinitionForItem(index, item.Uri, item.SelectionRange);
        if (def is null)
            return [];

        return
        [
            .. index
                .OutgoingCalls(def.QualifiedKey, def.File, def.Span)
                .Select(call => new CallHierarchyOutgoingCall
                {
                    To = ItemFor(call.Target, item.Uri),
                    FromRanges = new Container<
                        OmniSharp.Extensions.LanguageServer.Protocol.Models.Range
                    >(call.FromSpans.Select(TextDocumentSyncHandler.SpanToRange)),
                }),
        ];
    }

    private static CallHierarchyItem ItemFor(IndexedDefinition def, DocumentUri fallbackUri)
    {
        var range = TextDocumentSyncHandler.SpanToRange(def.Span);
        return new CallHierarchyItem
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
