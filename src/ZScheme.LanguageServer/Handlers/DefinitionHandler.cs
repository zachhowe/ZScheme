using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
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

        var span = ResolveDefinition(state, line, col, analysisService.Index);
        if (span is null)
            return Task.FromResult<LocationOrLocationLinks?>(null);

        var location = new Location
        {
            Uri = SpanUri(span.Value, request.TextDocument.Uri),
            Range = TextDocumentSyncHandler.SpanToRange(span.Value),
        };

        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(location));
    }

    /// <summary>
    ///     Test seam: resolve the defining span for the name at a 1-based (line, col)
    ///     position, consulting the workspace <paramref name="index" /> (when supplied)
    ///     for cross-file / cross-package definitions. Returns null if the cursor is not
    ///     on a Name node, or the name has no recorded definition (e.g. a parameter or an
    ///     unbound symbol). The returned span's <see cref="SourceSpan.File" /> identifies
    ///     the defining file, which may differ from the current document.
    /// </summary>
    public static SourceSpan? ResolveDefinition(
        DocumentState state,
        int line,
        int col,
        WorkspaceIndex? index = null
    )
    {
        return SymbolResolver.Resolve(state, index, line, col)?.DefinitionSpan;
    }

    /// <summary>Builds the LSP URI for a resolved span, using its defining file when
    ///     known (cross-file jumps) and falling back to the current document.</summary>
    internal static DocumentUri SpanUri(SourceSpan span, DocumentUri fallback)
    {
        return string.IsNullOrEmpty(span.File)
            ? fallback
            : DocumentUri.FromFileSystemPath(span.File);
    }
}
