using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

public sealed class DocumentFormattingHandler(AnalysisService analysisService)
    : DocumentFormattingHandlerBase
{
    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentFormattingCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DocumentFormattingRegistrationOptions
        {
            DocumentSelector = FormattingSupport.Selector,
        };
    }

    public override Task<TextEditContainer?> Handle(
        DocumentFormattingParams request,
        CancellationToken cancellationToken
    )
    {
        var edits = FormattingSupport.ComputeEdits(
            analysisService,
            request.TextDocument.Uri,
            request.Options
        );

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(edits));
    }
}
