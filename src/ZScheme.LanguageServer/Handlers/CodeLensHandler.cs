using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Serialization;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     A "N references" lens over each top-level definition, counted from the
///     workspace index. Clicking invokes the client-side
///     <c>editor.action.showReferences</c> command (VS Code's peek view; the
///     rust-analyzer pattern) with the definition position and reference locations —
///     no server-side <c>workspace/executeCommand</c> needed. Clients that don't know
///     the command render the title as plain text.
/// </summary>
public sealed class CodeLensHandler(AnalysisService analysisService) : CodeLensHandlerBase
{
    protected override CodeLensRegistrationOptions CreateRegistrationOptions(
        CodeLensCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new CodeLensRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                TextDocumentFilter.ForLanguage("zscheme"),
                TextDocumentFilter.ForPattern("**/*.zs"),
                TextDocumentFilter.ForPattern("**/*.zspkg")
            ),
            ResolveProvider = false,
        };
    }

    public override Task<CodeLensContainer?> Handle(
        CodeLensParams request,
        CancellationToken cancellationToken
    )
    {
        var state = analysisService.GetDocument(request.TextDocument.Uri.ToString());
        if (state is null)
            return Task.FromResult<CodeLensContainer?>(null);

        var filePath = request.TextDocument.Uri.GetFileSystemPath();
        var lenses = Compute(analysisService.Index, filePath, request.TextDocument.Uri);
        return Task.FromResult<CodeLensContainer?>(new CodeLensContainer(lenses));
    }

    public override Task<CodeLens> Handle(CodeLens request, CancellationToken cancellationToken)
    {
        // ResolveProvider = false: lenses are fully populated up front.
        return Task.FromResult(request);
    }

    /// <summary>Test seam: one lens per indexed top-level definition in the file,
    ///     counting distinct reference sites excluding the declaration itself.</summary>
    public static IReadOnlyList<CodeLens> Compute(
        WorkspaceIndex index,
        string filePath,
        DocumentUri documentUri
    )
    {
        var lenses = new List<CodeLens>();
        foreach (var def in index.DefinitionsInFile(filePath))
        {
            var locations = index
                .FindReferences(def.QualifiedKey, def.BareName, def.File)
                .Select(r => r.Span)
                .Where(span => span != def.Span)
                .Distinct()
                .Select(span => new Location
                {
                    Uri = DefinitionHandler.SpanUri(span, documentUri),
                    Range = TextDocumentSyncHandler.SpanToRange(span),
                })
                .ToList();

            var range = TextDocumentSyncHandler.SpanToRange(def.Span);
            lenses.Add(
                new CodeLens
                {
                    Range = range,
                    Command = new Command
                    {
                        Title =
                            locations.Count == 1 ? "1 reference" : $"{locations.Count} references",
                        // Client-side command: (uri, position, locations) opens the
                        // references peek. Serialized with the LSP serializer so the
                        // nested models get protocol casing.
                        Name = "editor.action.showReferences",
                        Arguments = JArray.FromObject(
                            new object[] { documentUri.ToString(), range.Start, locations },
                            LspSerializer.Instance.JsonSerializer
                        ),
                    },
                }
            );
        }

        return lenses;
    }
}
