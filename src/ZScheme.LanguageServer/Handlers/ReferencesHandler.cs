using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

public sealed class ReferencesHandler(AnalysisService analysisService) : ReferencesHandlerBase
{
    protected override ReferenceRegistrationOptions CreateRegistrationOptions(
        ReferenceCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new ReferenceRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
        };
    }

    public override Task<LocationContainer?> Handle(
        ReferenceParams request,
        CancellationToken cancellationToken
    )
    {
        var uri = request.TextDocument.Uri.ToString();
        var state = analysisService.GetDocument(uri);
        if (state is null)
            return Task.FromResult<LocationContainer?>(new LocationContainer());

        var line = request.Position.Line + 1;
        var col = request.Position.Character + 1;

        var locations = ResolveReferences(
            state,
            analysisService.Index,
            line,
            col,
            request.Context?.IncludeDeclaration ?? false,
            request.TextDocument.Uri
        );

        return Task.FromResult<LocationContainer?>(new LocationContainer(locations));
    }

    /// <summary>
    ///     Test seam: all references to the symbol under the cursor across the workspace.
    ///     Includes the declaration only when <paramref name="includeDeclaration" /> is set.
    /// </summary>
    public static IReadOnlyList<Location> ResolveReferences(
        DocumentState state,
        WorkspaceIndex index,
        int line,
        int col,
        bool includeDeclaration,
        OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri fallbackUri
    )
    {
        var resolved = SymbolResolver.Resolve(state, index, line, col);
        if (resolved is null)
            return [];

        var target = resolved.Value;
        var defSpan = target.DefinitionSpan;
        var references = index.FindReferences(target.QualifiedKey, target.BareName, defSpan.File);

        var locations = new List<Location>();
        var seen = new HashSet<(string, int, int, int)>();

        void Add(SourceSpan span)
        {
            if (seen.Add((span.File, span.Line, span.Column, span.Length)))
                locations.Add(
                    new Location
                    {
                        Uri = DefinitionHandler.SpanUri(span, fallbackUri),
                        Range = TextDocumentSyncHandler.SpanToRange(span),
                    }
                );
        }

        foreach (var reference in references)
            if (includeDeclaration || reference.Span != defSpan)
                Add(reference.Span);

        // Some declarations aren't collected as a Name occurrence (records, unions,
        // classes, interfaces have no synthesized name node), so add it explicitly.
        if (includeDeclaration)
            Add(defSpan);

        return locations;
    }
}
