namespace ZScheme.LanguageServer.Handlers;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;
using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

public sealed class DocumentSymbolHandler(AnalysisService analysisService) : DocumentSymbolHandlerBase
{
    protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(
        DocumentSymbolCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg"))
        };

    public override Task<SymbolInformationOrDocumentSymbolContainer?> Handle(
        DocumentSymbolParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(null);

        var symbols = state.Symbols
            .Where(s => s.Kind is not (Analysis.SymbolKind.Parameter or Analysis.SymbolKind.Variable))
            .Select(s => new SymbolInformationOrDocumentSymbol(new DocumentSymbol
            {
                Name = s.Name,
                Kind = MapSymbolKind(s.Kind),
                Detail = s.ResolvedType?.ToString(),
                Range = TextDocumentSyncHandler.SpanToRange(s.DefinitionSpan),
                SelectionRange = TextDocumentSyncHandler.SpanToRange(s.DefinitionSpan)
            }))
            .ToArray();

        return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(
            new SymbolInformationOrDocumentSymbolContainer(symbols));
    }

    private static LspSymbolKind MapSymbolKind(Analysis.SymbolKind kind) => kind switch
    {
        Analysis.SymbolKind.Function => LspSymbolKind.Function,
        Analysis.SymbolKind.Variable => LspSymbolKind.Variable,
        Analysis.SymbolKind.Record => LspSymbolKind.Struct,
        Analysis.SymbolKind.Union => LspSymbolKind.Enum,
        Analysis.SymbolKind.UnionCase => LspSymbolKind.EnumMember,
        Analysis.SymbolKind.Class => LspSymbolKind.Class,
        Analysis.SymbolKind.Interface => LspSymbolKind.Interface,
        Analysis.SymbolKind.Module => LspSymbolKind.Module,
        Analysis.SymbolKind.Parameter => LspSymbolKind.Variable,
        _ => LspSymbolKind.Variable
    };
}
