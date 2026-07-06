using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;
using SymbolKind = ZScheme.LanguageServer.Analysis.SymbolKind;

namespace ZScheme.LanguageServer.Handlers;

public sealed class DocumentSymbolHandler(AnalysisService analysisService)
    : DocumentSymbolHandlerBase
{
    protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(
        DocumentSymbolCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DocumentSymbolRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<SymbolInformationOrDocumentSymbolContainer?> Handle(
        DocumentSymbolParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(null);

        var symbols = state
            .Symbols.Where(s => s.Kind is not (SymbolKind.Parameter or SymbolKind.Variable))
            .Select(s => new SymbolInformationOrDocumentSymbol(
                new DocumentSymbol
                {
                    Name = s.Name,
                    Kind = SymbolKindMapper.ToLsp(s.Kind),
                    Detail = s.ResolvedType?.ToString(),
                    Range = TextDocumentSyncHandler.SpanToRange(s.DefinitionSpan),
                    SelectionRange = TextDocumentSyncHandler.SpanToRange(s.DefinitionSpan),
                }
            ))
            .ToArray();

        return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(
            new SymbolInformationOrDocumentSymbolContainer(symbols)
        );
    }
}
