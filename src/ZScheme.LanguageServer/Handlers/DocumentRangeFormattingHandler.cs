using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>Formats the whole document and returns only the edits that touch the selection,
///     so a "format selection" can never lay code out differently from a "format document".</summary>
public sealed class DocumentRangeFormattingHandler(AnalysisService analysisService)
    : DocumentRangeFormattingHandlerBase
{
    protected override DocumentRangeFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentRangeFormattingCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DocumentRangeFormattingRegistrationOptions
        {
            DocumentSelector = FormattingSupport.Selector,
        };
    }

    public override Task<TextEditContainer> Handle(
        DocumentRangeFormattingParams request,
        CancellationToken cancellationToken
    )
    {
        var edits = FormattingSupport.ComputeEdits(
            analysisService,
            request.TextDocument.Uri,
            request.Options,
            request.Range
        );

        return Task.FromResult(new TextEditContainer(edits));
    }
}
