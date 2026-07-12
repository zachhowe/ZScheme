using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>A "N references" lens over each top-level definition, counted from the
///     workspace index. The command is informational (not clickable): invoking a peek
///     view needs <c>workspace/executeCommand</c>, which the server doesn't offer yet.</summary>
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
        var lenses = Compute(analysisService.Index, filePath);
        return Task.FromResult<CodeLensContainer?>(new CodeLensContainer(lenses));
    }

    public override Task<CodeLens> Handle(CodeLens request, CancellationToken cancellationToken)
    {
        // ResolveProvider = false: lenses are fully populated up front.
        return Task.FromResult(request);
    }

    /// <summary>Test seam: one lens per indexed top-level definition in the file,
    ///     counting distinct reference sites excluding the declaration itself.</summary>
    public static IReadOnlyList<CodeLens> Compute(WorkspaceIndex index, string filePath)
    {
        var lenses = new List<CodeLens>();
        foreach (var def in index.DefinitionsInFile(filePath))
        {
            var count = index
                .FindReferences(def.QualifiedKey, def.BareName, def.File)
                .Select(r => r.Span)
                .Where(span => span != def.Span)
                .Distinct()
                .Count();

            lenses.Add(
                new CodeLens
                {
                    Range = TextDocumentSyncHandler.SpanToRange(def.Span),
                    Command = new Command
                    {
                        Title = count == 1 ? "1 reference" : $"{count} references",
                        Name = "",
                    },
                }
            );
        }

        return lenses;
    }
}
