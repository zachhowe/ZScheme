namespace ZScript.LanguageServer.Handlers;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScript.Compiler.Ast;
using ZScript.LanguageServer.Analysis;

public sealed class DefinitionHandler(AnalysisService analysisService) : DefinitionHandlerBase
{
    protected override DefinitionRegistrationOptions CreateRegistrationOptions(
        DefinitionCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscript"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg"))
        };

    public override Task<LocationOrLocationLinks?> Handle(
        DefinitionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state?.Ast is null)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var node = HoverHandler.FindNodeAt(state.Ast, line, col);
        if (node is not AstNode.Name name)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        if (!state.NameToDefinition.TryGetValue(name.Value, out var symbol))
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var location = new Location
        {
            Uri = request.TextDocument.Uri,
            Range = TextDocumentSyncHandler.SpanToRange(symbol.DefinitionSpan)
        };

        return Task.FromResult<LocationOrLocationLinks?>(
            new LocationOrLocationLinks(location));
    }
}
