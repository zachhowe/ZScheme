using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Server;
using ZScheme.LanguageServer;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;

var server = await LanguageServer.From(options =>
{
    options
        .WithInput(Console.OpenStandardInput())
        .WithOutput(Console.OpenStandardOutput())
        .WithHandler<TextDocumentSyncHandler>()
        .WithHandler<HoverHandler>()
        .WithHandler<DefinitionHandler>()
        .WithHandler<ReferencesHandler>()
        .WithHandler<DocumentSymbolHandler>()
        .WithHandler<WorkspaceSymbolHandler>()
        .WithHandler<CompletionHandler>()
        .WithHandler<PrepareRenameHandler>()
        .WithHandler<RenameHandler>()
        .WithHandler<DocumentHighlightHandler>()
        .WithHandler<InlayHintHandler>()
        .WithHandler<SignatureHelpHandler>()
        .WithHandler<DidChangeWatchedFilesHandler>()
        .WithHandler<CodeActionHandler>()
        .WithHandler<FoldingRangeHandler>()
        .WithHandler<SelectionRangeHandler>()
        .WithHandler<SemanticTokensHandler>()
        .WithHandler<TypeDefinitionHandler>()
        .WithHandler<ImplementationHandler>()
        .WithHandler<DocumentLinkHandler>()
        .WithHandler<CodeLensHandler>()
        .WithServices(services =>
        {
            services.AddSingleton<AnalysisService>();
        });
});

// Seed the workspace symbol index from the client's workspace roots so cross-file
// navigation works into files the user has not opened yet.
var analysisService = server.GetRequiredService<AnalysisService>();
var roots = new List<string>();
if (server.ClientSettings.WorkspaceFolders is { } folders)
    roots.AddRange(
        folders
            .Select(f => f.Uri.GetFileSystemPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
    );
if (roots.Count == 0 && server.ClientSettings.RootUri is { } rootUri)
{
    var rootPath = rootUri.GetFileSystemPath();
    if (!string.IsNullOrEmpty(rootPath))
        roots.Add(rootPath);
}

// Discarded: the scan runs in the background while the server serves requests; the
// reporter surfaces it as a window/workDoneProgress indicator when supported.
_ = analysisService.InitializeWorkspaceAsync(
    roots,
    new WorkspaceScanProgressReporter(server.WorkDoneManager)
);

await server.WaitForExit;
