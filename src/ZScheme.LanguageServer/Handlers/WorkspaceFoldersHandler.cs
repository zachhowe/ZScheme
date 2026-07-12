using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     Keeps the workspace index in step with <c>workspace/didChangeWorkspaceFolders</c>:
///     added folders get a background scan (same path as the startup scan, no progress
///     reporting), removed folders have their files purged from the index. Open buffers
///     under a removed root keep working — they re-index on edit like any open document.
/// </summary>
public sealed class WorkspaceFoldersHandler(AnalysisService analysisService)
    : DidChangeWorkspaceFoldersHandlerBase
{
    protected override DidChangeWorkspaceFolderRegistrationOptions CreateRegistrationOptions(
        ClientCapabilities clientCapabilities
    )
    {
        return new DidChangeWorkspaceFolderRegistrationOptions
        {
            Supported = true,
            ChangeNotifications = true,
        };
    }

    public override Task<Unit> Handle(
        DidChangeWorkspaceFoldersParams request,
        CancellationToken cancellationToken
    )
    {
        foreach (var removed in request.Event.Removed)
            if (TryPath(removed) is { } removedPath)
                analysisService.PurgeRoot(removedPath);

        var added = request
            .Event.Added.Select(TryPath)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();
        if (added.Count > 0)
            _ = analysisService.ScanAdditionalRootsAsync(added);

        return Unit.Task;
    }

    private static string? TryPath(WorkspaceFolder folder)
    {
        try
        {
            var path = folder.Uri.GetFileSystemPath();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }
}
