using MediatR;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;
using Diagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Handlers;

using DiagnosticSeverity = OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity;

public sealed class TextDocumentSyncHandler(
    AnalysisService analysisService,
    ILanguageServerFacade server
) : TextDocumentSyncHandlerBase
{
    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new TextDocumentAttributes(uri, "zscheme");
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new TextDocumentSyncRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            Change = TextDocumentSyncKind.Full,
        };
    }

    public override async Task<Unit> Handle(
        DidOpenTextDocumentParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.AnalyzeImmediate(
            uri,
            request.TextDocument.Text,
            request.TextDocument.Version ?? 0
        );
        PublishDiagnostics(request.TextDocument.Uri, state);
        return Unit.Value;
    }

    public override async Task<Unit> Handle(
        DidChangeTextDocumentParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var text = request.ContentChanges.FirstOrDefault()?.Text ?? "";
        var state = await analysisService.AnalyzeAsync(
            uri,
            text,
            request.TextDocument.Version ?? 0
        );
        PublishDiagnostics(request.TextDocument.Uri, state);
        return Unit.Value;
    }

    public override Task<Unit> Handle(
        DidSaveTextDocumentParams request,
        CancellationToken cancellationToken
    )
    {
        return Unit.Task;
    }

    public override Task<Unit> Handle(
        DidCloseTextDocumentParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        analysisService.RemoveDocument(uri);

        // The closed buffer may have diverged from disk (unsaved edits); re-sync the
        // workspace index with the on-disk truth now that the buffer no longer wins.
        try
        {
            var path = request.TextDocument.Uri.GetFileSystemPath();
            if (!string.IsNullOrEmpty(path))
            {
                if (File.Exists(path))
                    analysisService.ReindexFromDisk(path);
                else
                    analysisService.RemoveFromIndex(path);
            }
        }
        catch
        {
            // Non-file URIs / IO failures: index re-sync is best-effort.
        }

        server.TextDocument.PublishDiagnostics(
            new PublishDiagnosticsParams
            {
                Uri = request.TextDocument.Uri,
                Diagnostics = new Container<Diagnostic>(),
            }
        );
        return Unit.Task;
    }

    private void PublishDiagnostics(DocumentUri uri, DocumentState state)
    {
        server.TextDocument.PublishDiagnostics(
            new PublishDiagnosticsParams
            {
                Uri = uri,
                Diagnostics = new Container<Diagnostic>(ConvertDiagnostics(uri, state)),
            }
        );
    }

    /// <summary>Codes whose diagnostics clients should render de-emphasized (greyed
    ///     out) or struck through, per the LSP tag semantics.</summary>
    private static readonly Dictionary<string, DiagnosticTag[]> TagsByCode = new(
        StringComparer.Ordinal
    )
    {
        [DiagnosticCodes.UnusedBinding] = [DiagnosticTag.Unnecessary],
        [DiagnosticCodes.RedundantTypeQualifier] = [DiagnosticTag.Unnecessary],
        [DiagnosticCodes.DeprecatedAccessorSyntax] = [DiagnosticTag.Deprecated],
        [DiagnosticCodes.DeprecatedKeyword] = [DiagnosticTag.Deprecated],
    };

    /// <summary>Test seam: the LSP diagnostics published for a document, including
    ///     code, structured data, tags, and related information.</summary>
    public static Diagnostic[] ConvertDiagnostics(DocumentUri uri, DocumentState state)
    {
        return
        [
            .. state.Diagnostics.Diagnostics.Select(d => new Diagnostic
            {
                Range = SpanToRange(d.Span),
                Severity = d.Severity switch
                {
                    Compiler.Diagnostics.DiagnosticSeverity.Error => DiagnosticSeverity.Error,
                    Compiler.Diagnostics.DiagnosticSeverity.Hint => DiagnosticSeverity.Hint,
                    _ => DiagnosticSeverity.Warning,
                },
                Source = "zscheme",
                Message = d.Message,
                Code = d.Code is null ? (DiagnosticCode?)null : new DiagnosticCode(d.Code),
                Data = d.Data is null ? null! : JArray.FromObject(d.Data),
                Tags =
                    d.Code is not null && TagsByCode.TryGetValue(d.Code, out var tags)
                        ? new Container<DiagnosticTag>(tags)
                        : null,
                RelatedInformation = d.Related is null
                    ? null
                    : new Container<DiagnosticRelatedInformation>(
                        d.Related.Select(r => new DiagnosticRelatedInformation
                        {
                            Location = new Location
                            {
                                Uri = string.IsNullOrEmpty(r.Span.File)
                                    ? uri
                                    : DocumentUri.FromFileSystemPath(r.Span.File),
                                Range = SpanToRange(r.Span),
                            },
                            Message = r.Message,
                        })
                    ),
            }),
        ];
    }

    public static Range SpanToRange(SourceSpan span)
    {
        return new Range(
            new Position(Math.Max(0, span.Line - 1), Math.Max(0, span.Column - 1)),
            new Position(Math.Max(0, span.Line - 1), Math.Max(0, span.Column - 1 + span.Length))
        );
    }
}
