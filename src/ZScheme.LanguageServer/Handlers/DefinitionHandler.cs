using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

public sealed class DefinitionHandler(AnalysisService analysisService) : DefinitionHandlerBase
{
    protected override DefinitionRegistrationOptions CreateRegistrationOptions(
        DefinitionCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<LocationOrLocationLinks?> Handle(
        DefinitionParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var span = ResolveDefinition(state, line, col);
        if (span is null)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var location = new Location
        {
            Uri = request.TextDocument.Uri,
            Range = TextDocumentSyncHandler.SpanToRange(span.Value),
        };

        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(location));
    }

    /// <summary>
    ///     Test seam: resolve the defining span for the name at a 1-based (line, col)
    ///     position. Returns null if the cursor is not on a Name node, or the name has
    ///     no recorded definition (e.g. a parameter or an unbound symbol).
    /// </summary>
    public static SourceSpan? ResolveDefinition(DocumentState state, int line, int col)
    {
        if (state.Ast is null)
            return null;

        var node = HoverHandler.FindNodeAt(state.Ast, line, col);
        if (node is not AstNode.Name name)
            return null;

        if (!state.NameToDefinition.TryGetValue(name.Value, out var symbol))
            return null;

        return symbol.DefinitionSpan;
    }
}
