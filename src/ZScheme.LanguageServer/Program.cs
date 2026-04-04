using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Server;
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
        .WithHandler<DocumentSymbolHandler>()
        .WithHandler<CompletionHandler>()
        .WithServices(services => { services.AddSingleton<AnalysisService>(); });
});

await server.WaitForExit;
