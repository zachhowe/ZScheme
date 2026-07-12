using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using ZScheme.LanguageServer.Analysis;
using FileSystemWatcher = OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher;

namespace ZScheme.LanguageServer.Handlers;

/// <summary>
///     Keeps the workspace index in sync with on-disk changes to files the user has not
///     opened (external edits, new/deleted files, branch switches, <c>git pull</c>).
///     Without this the index is only refreshed by the one-time startup scan and by open
///     editor buffers. Relies on client-side file watching (VS Code supplies the events);
///     clients without watch support keep the startup-scan-only behavior.
/// </summary>
public sealed class DidChangeWatchedFilesHandler(AnalysisService analysisService)
    : DidChangeWatchedFilesHandlerBase
{
    protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(
        DidChangeWatchedFilesCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        return new DidChangeWatchedFilesRegistrationOptions
        {
            Watchers = new Container<FileSystemWatcher>(
                new FileSystemWatcher
                {
                    GlobPattern = new GlobPattern("**/*.zs"),
                    Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete,
                },
                new FileSystemWatcher
                {
                    GlobPattern = new GlobPattern("**/*.zspkg"),
                    Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete,
                }
            ),
        };
    }

    public override Task<Unit> Handle(
        DidChangeWatchedFilesParams request,
        CancellationToken cancellationToken
    )
    {
        foreach (var change in request.Changes)
        {
            string? path;
            try
            {
                path = change.Uri.GetFileSystemPath();
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(path))
                continue;

            if (path.EndsWith(".zspkg", StringComparison.OrdinalIgnoreCase))
            {
                ReindexPackageFiles(path);
                continue;
            }

            if (change.Type == FileChangeType.Deleted)
                analysisService.RemoveFromIndex(path);
            else
                _ = analysisService.QueueReindexAsync(path);
        }

        return Unit.Task;
    }

    /// <summary>
    ///     A manifest change (dependencies, default-module, source dirs) can alter how every
    ///     file in the package type-checks, so re-queue all indexed files under the
    ///     manifest's directory. Package config is re-discovered per compile, so recompiling
    ///     is sufficient to pick up the new manifest.
    /// </summary>
    private void ReindexPackageFiles(string manifestPath)
    {
        string packageDir;
        try
        {
            packageDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "";
        }
        catch
        {
            return;
        }

        if (packageDir.Length == 0)
            return;

        var prefix = packageDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var indexed in analysisService.Index.IndexedFiles)
            if (indexed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _ = analysisService.QueueReindexAsync(indexed);
    }
}
