using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     <c>textDocument/formatting</c>. Delegates to the injected
///     <see cref="ISourceFormatter" /> — a null result (the default until the formatter
///     branch lands) declines the request, leaving the document untouched.
/// </summary>
public sealed class DocumentFormattingHandler(
    AnalysisService analysisService,
    ISourceFormatter formatter
) : DocumentFormattingHandlerBase
{
    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentFormattingCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DocumentFormattingRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<TextEditContainer?> Handle(
        DocumentFormattingParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<TextEditContainer?>(null);

        var edits = formatter.Format(state.Source, range: null);
        return Task.FromResult<TextEditContainer?>(
            edits is null ? null : new TextEditContainer(edits)
        );
    }
}

/// <summary>
///     <c>textDocument/rangeFormatting</c>. Same contract as
///     <see cref="DocumentFormattingHandler" />, scoped to the requested range.
/// </summary>
public sealed class DocumentRangeFormattingHandler(
    AnalysisService analysisService,
    ISourceFormatter formatter
) : DocumentRangeFormattingHandlerBase
{
    protected override DocumentRangeFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentRangeFormattingCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DocumentRangeFormattingRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    // The OmniSharp range/on-type bases model the LSP result as non-nullable, so a
    // decline (formatter returned null, or no open document) is an empty edit list —
    // semantically identical for clients: the document is left untouched.
    public override Task<TextEditContainer> Handle(
        DocumentRangeFormattingParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        var edits = state is null ? null : formatter.Format(state.Source, request.Range);
        return Task.FromResult(
            edits is null ? new TextEditContainer() : new TextEditContainer(edits)
        );
    }
}

/// <summary>
///     <c>textDocument/onTypeFormatting</c>. Same contract as
///     <see cref="DocumentFormattingHandler" />, triggered while typing; the LSP result
///     is an edit list, so the formatter's edits flow through unchanged, and null
///     declines.
/// </summary>
public sealed class DocumentOnTypeFormattingHandler(
    AnalysisService analysisService,
    ISourceFormatter formatter
) : DocumentOnTypeFormattingHandlerBase
{
    protected override DocumentOnTypeFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentOnTypeFormattingCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DocumentOnTypeFormattingRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            FirstTriggerCharacter = "\n",
        };
    }

    // The on-type base (unlike range) models the LSP result as nullable, so null
    // declines cleanly.
    public override Task<TextEditContainer?> Handle(
        DocumentOnTypeFormattingParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<TextEditContainer?>(null);

        var edits = formatter.Format(state.Source, null);
        return Task.FromResult<TextEditContainer?>(
            edits is null ? null : new TextEditContainer(edits)
        );
    }
}
