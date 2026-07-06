using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

public sealed class WorkspaceSymbolHandler(AnalysisService analysisService)
    : WorkspaceSymbolsHandlerBase
{
    private const int MaxResults = 1000;

    protected override WorkspaceSymbolRegistrationOptions CreateRegistrationOptions(
        WorkspaceSymbolCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new WorkspaceSymbolRegistrationOptions();
    }

    public override Task<Container<WorkspaceSymbol>?> Handle(
        WorkspaceSymbolParams request,
        CancellationToken cancellationToken
    )
    {
        var matches = analysisService.Index.SearchSymbols(request.Query ?? string.Empty);

        var symbols = matches
            .Where(d => !string.IsNullOrEmpty(d.File))
            .Take(MaxResults)
            .Select(d => new WorkspaceSymbol
            {
                Name = d.BareName,
                Kind = SymbolKindMapper.ToLsp(d.Kind),
                ContainerName = d.ContainerModule,
                Location = new Location
                {
                    Uri = DocumentUri.FromFileSystemPath(d.File),
                    Range = TextDocumentSyncHandler.SpanToRange(d.Span),
                },
            })
            .ToArray();

        return Task.FromResult<Container<WorkspaceSymbol>?>(
            new Container<WorkspaceSymbol>(symbols)
        );
    }
}
