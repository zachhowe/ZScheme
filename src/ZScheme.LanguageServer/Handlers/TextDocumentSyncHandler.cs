namespace ZScheme.LanguageServer.Handlers;

using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using ZScheme.LanguageServer.Analysis;
using DiagnosticSeverity = OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity;

public sealed class TextDocumentSyncHandler(
    AnalysisService analysisService,
    ILanguageServerFacade server) : TextDocumentSyncHandlerBase
{
    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri) =>
        new(uri, "zscheme");

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")),
            Change = TextDocumentSyncKind.Full
        };

    public override async Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.AnalyzeImmediate(uri, request.TextDocument.Text, request.TextDocument.Version ?? 0);
        PublishDiagnostics(request.TextDocument.Uri, state);
        return Unit.Value;
    }

    public override async Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToString();
        var text = request.ContentChanges.FirstOrDefault()?.Text ?? "";
        var state = await analysisService.AnalyzeAsync(uri, text, request.TextDocument.Version ?? 0);
        PublishDiagnostics(request.TextDocument.Uri, state);
        return Unit.Value;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken) =>
        Unit.Task;

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToString();
        analysisService.RemoveDocument(uri);
        server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = request.TextDocument.Uri,
            Diagnostics = new Container<Diagnostic>()
        });
        return Unit.Task;
    }

    private void PublishDiagnostics(DocumentUri uri, DocumentState state)
    {
        var diagnostics = state.Diagnostics.Diagnostics
            .Select(d => new Diagnostic
            {
                Range = SpanToRange(d.Span),
                Severity = d.Severity == Compiler.Diagnostics.DiagnosticSeverity.Error
                    ? DiagnosticSeverity.Error
                    : DiagnosticSeverity.Warning,
                Source = "zscheme",
                Message = d.Message
            })
            .ToArray();

        server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = new Container<Diagnostic>(diagnostics)
        });
    }

    internal static OmniSharp.Extensions.LanguageServer.Protocol.Models.Range SpanToRange(
        Compiler.Diagnostics.SourceSpan span) =>
        new(
            new Position(Math.Max(0, span.Line - 1), Math.Max(0, span.Column - 1)),
            new Position(Math.Max(0, span.Line - 1), Math.Max(0, span.Column - 1 + span.Length)));
}
