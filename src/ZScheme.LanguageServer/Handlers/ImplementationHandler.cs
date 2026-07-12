using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Ast;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>Jump from an interface to its implementors: classes declaring it in
///     <c>define-class</c>, plus interfaces extending it (and their implementors,
///     transitively) via the workspace index's implementations facet.</summary>
public sealed class ImplementationHandler(AnalysisService analysisService)
    : ImplementationHandlerBase
{
    protected override ImplementationRegistrationOptions CreateRegistrationOptions(
        ImplementationCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new ImplementationRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<LocationOrLocationLinks?> Handle(
        ImplementationParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var implementations = Resolve(
            state,
            analysisService.Index,
            request.Position.Line + 1,
            request.Position.Character + 1
        );
        if (implementations.Count == 0)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var locations = implementations.Select(def => new LocationOrLocationLink(
            new Location
            {
                Uri = DefinitionHandler.SpanUri(def.Span, request.TextDocument.Uri),
                Range = TextDocumentSyncHandler.SpanToRange(def.Span),
            }
        ));
        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(locations));
    }

    /// <summary>Test seam: implementors of the interface name at the 1-based
    ///     (line, col). The cursor may be on a usage (a Name node) or on the
    ///     declaration name inside <c>define-interface</c> — decl names have no AST
    ///     node, so those fall back to lexical identifier extraction.</summary>
    public static IReadOnlyList<IndexedDefinition> Resolve(
        DocumentState state,
        WorkspaceIndex index,
        int line,
        int col
    )
    {
        string? name = null;
        if (
            state.Ast is not null
            && AstNavigation.FindNodeAt(state.Ast, line, col) is AstNode.Name nameNode
        )
            name = nameNode.Value;
        else
            name = SourceText.IdentifierAt(
                state.Source,
                SourceText.OffsetAt(state.Source, line - 1, col - 1)
            );

        if (string.IsNullOrEmpty(name))
            return [];

        var bare = name[(Math.Max(name.LastIndexOf('/'), name.LastIndexOf('.')) + 1)..];
        return index.FindImplementations(bare);
    }
}
