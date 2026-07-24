using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     <c>textDocument/declaration</c>. ZScheme has no declaration/definition split —
///     there are no forward declarations or header files, so a name's declaration
///     <em>is</em> its definition. This delegates to <see cref="DefinitionHandler" />
///     rather than declining the request, so a client's "Go to Declaration" lands
///     somewhere useful instead of erroring.
/// </summary>
public sealed class DeclarationHandler(AnalysisService analysisService) : DeclarationHandlerBase
{
    protected override DeclarationRegistrationOptions CreateRegistrationOptions(
        DeclarationCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DeclarationRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<LocationOrLocationLinks?> Handle(
        DeclarationParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var span = DefinitionHandler.ResolveDefinition(
            state,
            request.Position.Line + 1,
            request.Position.Character + 1,
            analysisService.Index
        );
        if (span is null)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var location = new Location
        {
            Uri = DefinitionHandler.SpanUri(span.Value, request.TextDocument.Uri),
            Range = TextDocumentSyncHandler.SpanToRange(span.Value),
        };

        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(location));
    }
}
