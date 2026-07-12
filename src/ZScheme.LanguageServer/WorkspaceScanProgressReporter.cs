using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.WorkDone;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer;

/// <summary>
///     Surfaces the startup workspace scan as a <c>window/workDoneProgress</c>
///     indicator. Everything is best-effort: if the client doesn't support progress
///     (or the observer isn't ready yet when a report arrives), updates are dropped
///     silently — indexing itself must never be disturbed.
/// </summary>
public sealed class WorkspaceScanProgressReporter(IServerWorkDoneManager manager)
    : IWorkspaceScanReporter
{
    private Task<IWorkDoneObserver>? _observer;
    private int _lastPercentage = -1;

    public void Begin(int totalFiles)
    {
        try
        {
            if (!manager.IsSupported || totalFiles == 0)
                return;
            _observer = manager.Create(
                new WorkDoneProgressBegin
                {
                    Title = "Indexing ZScheme workspace",
                    Message = $"{totalFiles} files",
                    Percentage = 0,
                }
            );
        }
        catch
        {
            _observer = null;
        }
    }

    public void Report(int processedFiles, int totalFiles, string currentFile)
    {
        var percentage = (int)(100.0 * processedFiles / Math.Max(1, totalFiles));
        if (percentage == _lastPercentage)
            return;
        _lastPercentage = percentage;
        WithObserver(o => o.OnNext(currentFile, percentage, cancellable: false));
    }

    public void End()
    {
        WithObserver(o => o.OnCompleted());
    }

    private void WithObserver(Action<IWorkDoneObserver> action)
    {
        var task = _observer;
        if (task is null)
            return;
        _ = task.ContinueWith(
            t =>
            {
                if (!t.IsCompletedSuccessfully)
                    return;
                try
                {
                    action(t.Result);
                }
                catch
                {
                    // Progress is best-effort.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }
}
