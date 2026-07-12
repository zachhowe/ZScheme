namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Progress sink for the startup workspace scan. Keeps
///     <see cref="AnalysisService" /> free of LSP types — the server wires in an
///     implementation backed by <c>window/workDoneProgress</c>; tests use a recording
///     fake. Implementations must not throw: progress is best-effort.
/// </summary>
public interface IWorkspaceScanReporter
{
    void Begin(int totalFiles);

    void Report(int processedFiles, int totalFiles, string currentFile);

    void End();
}
