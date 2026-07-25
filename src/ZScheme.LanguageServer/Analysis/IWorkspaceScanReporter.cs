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

    /// <summary>
    ///     Reports that <paramref name="currentFilePath" /> — a full path, spelled as the
    ///     index spells it — survived the exclusion rules and is about to be read.
    ///     Shortening it for display is the sink's job: the full path is what lets a caller
    ///     distinguish two same-named files in different directories, which a bare file
    ///     name cannot.
    /// </summary>
    void Report(int processedFiles, int totalFiles, string currentFilePath);

    void End();
}
