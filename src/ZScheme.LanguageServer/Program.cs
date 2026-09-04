using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Shared;
using OmniSharp.Extensions.LanguageServer.Server;
using ZScheme.LanguageServer;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;

// `--debug` mirrors the zs CLI's global flag. All logging goes to stderr — stdout is the
// JSON-RPC channel (see StderrLogging).
var debugLogging = args.Contains("--debug");
StderrLogging.Configure(debugLogging);

// Holds a didOpen that races the initialize handshake instead of dropping it, which is what
// the stock receiver does to a client that pipelines its startup. See HandshakeAwareReceiver.
var receiver = new HandshakeAwareReceiver();

var server = await LanguageServer.From(options =>
{
    options
        .WithInput(Console.OpenStandardInput())
        .WithOutput(Console.OpenStandardOutput())
        .WithReceiver(receiver)
        .ConfigureLogging(builder => StderrLogging.AddStderr(builder, debugLogging))
        // Runs before OmniSharp derives the server capabilities, so clearing the client's
        // dynamicRegistration flags here makes every capability land in the initialize
        // result instead of a later client/registerCapability. See StaticCapabilities.
        .OnInitialize(
            (_, request, _) =>
            {
                StaticCapabilities.ForceStatic(request.Capabilities);
                return Task.CompletedTask;
            }
        )
        .WithHandler<TextDocumentSyncHandler>()
        .WithHandler<HoverHandler>()
        .WithHandler<DefinitionHandler>()
        .WithHandler<DeclarationHandler>()
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
        .WithHandler<CallHierarchyHandler>()
        .WithHandler<TypeHierarchyHandler>()
        .WithHandler<WorkspaceFoldersHandler>()
        .WithHandler<DocumentFormattingHandler>()
        .WithHandler<DocumentRangeFormattingHandler>()
        .WithServices(services =>
        {
            services.AddSingleton<AnalysisService>();
            // A Receiver is also the output filter that gates server-to-client traffic on
            // initialization. Registering only IReceiver leaves DI to build a second,
            // never-initialized LspServerReceiver for IOutputFilter, which then silences
            // every message the server sends.
            services.AddSingleton<IOutputFilter>(receiver);
        });
});

// Handlers are registered by now, so anything a pipelining client sent during the handshake
// can finally be routed. Ahead of the workspace scan: a held didOpen is a document the user
// is looking at right now.
await receiver.ReplayHeldNotificationsAsync(
    server.GetRequiredService<IRequestRouter<ILspHandlerDescriptor>>()
);

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
