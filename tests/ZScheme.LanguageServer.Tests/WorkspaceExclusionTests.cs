using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

/// <summary>
///     The startup scan must not index generated trees. The motivating case: the fuzzer
///     writes thousands of <c>original.zs</c> repro dumps under the gitignored
///     <c>fuzz-runs/</c>, which used to dominate both the scan and the symbol index.
/// </summary>
public sealed class WorkspaceExclusionTests
{
    /// <summary>No-logic recording fake (call recording only, per docs/MOCKS.md).</summary>
    private sealed class RecordingReporter : IWorkspaceScanReporter
    {
        public List<int> BeginCalls { get; } = [];
        public List<string> ReportedFiles { get; } = [];

        public void Begin(int totalFiles) => BeginCalls.Add(totalFiles);

        public void Report(int processedFiles, int totalFiles, string currentFile) =>
            ReportedFiles.Add(currentFile);

        public void End() { }
    }

    private static TempPackageWorkspace NewWorkspace() =>
        new(
            "expkg",
            new Dictionary<string, string> { ["one.zs"] = "(module one)\n(define x 1)\n(export x)" }
        );

    [Fact]
    public async Task GitIgnoredTree_IsNeitherScannedNorIndexed()
    {
        using var ws = NewWorkspace();
        ws.WriteRootFile(".gitignore", "# generated\nfuzz-runs/\n");
        var artifact = ws.WriteRootFile(
            Path.Combine("fuzz-runs", "20260706-seedf00d", "artifacts", "f-1", "original.zs"),
            "(define generated 1)"
        );
        var reporter = new RecordingReporter();

        await ws.Service.InitializeWorkspaceAsync([ws.Root], reporter);

        Assert.Equal(1, Assert.Single(reporter.BeginCalls));
        Assert.DoesNotContain("original.zs", reporter.ReportedFiles);
        Assert.False(ws.Service.Index.Contains(artifact));
        Assert.True(ws.Service.Index.Contains(ws.PathOf("one.zs")));
    }

    [Fact]
    public async Task NestedGitIgnore_AnchorsToItsOwnDirectory()
    {
        using var ws = NewWorkspace();
        ws.WriteRootFile(Path.Combine("editor", ".gitignore"), "/grammars\n");
        var ignored = ws.WriteRootFile(
            Path.Combine("editor", "grammars", "sample.zs"),
            "(define g 1)"
        );
        var kept = ws.WriteRootFile(Path.Combine("tools", "grammars", "sample.zs"), "(define t 1)");
        var reporter = new RecordingReporter();

        await ws.Service.InitializeWorkspaceAsync([ws.Root], reporter);

        Assert.False(ws.Service.Index.Contains(ignored));
        Assert.True(ws.Service.Index.Contains(kept));
    }

    [Fact]
    public async Task Negation_ReIncludesAFile()
    {
        using var ws = NewWorkspace();
        ws.WriteRootFile(".gitignore", "*.gen.zs\n!keep.gen.zs\n");
        var dropped = ws.WriteRootFile("dropped.gen.zs", "(define d 1)");
        var kept = ws.WriteRootFile("keep.gen.zs", "(define k 1)");

        await ws.Service.InitializeWorkspaceAsync([ws.Root], null);

        Assert.False(ws.Service.Index.Contains(dropped));
        Assert.True(ws.Service.Index.Contains(kept));
    }

    [Fact]
    public async Task GeneratedDirectoryNames_AreSkippedWithoutAnyGitIgnore()
    {
        using var ws = NewWorkspace();
        var generated = new[]
        {
            "bin",
            "obj",
            "node_modules",
            "target",
            "dist",
            "coverage",
            "TestResults",
            ".cache",
        }
            .Select(dir => ws.WriteRootFile(Path.Combine(dir, "gen.zs"), "(define g 1)"))
            .ToList();

        await ws.Service.InitializeWorkspaceAsync([ws.Root], null);

        Assert.All(generated, path => Assert.False(ws.Service.Index.Contains(path)));
        Assert.True(ws.Service.Index.Contains(ws.PathOf("one.zs")));
    }

    [Fact]
    public async Task WatcherEvents_ForIgnoredFiles_AreNoOps()
    {
        using var ws = NewWorkspace();
        ws.WriteRootFile(".gitignore", "fuzz-runs/\n");
        var artifact = ws.WriteRootFile(
            Path.Combine("fuzz-runs", "run-1", "original.zs"),
            "(define generated 1)"
        );

        await ws.Service.InitializeWorkspaceAsync([ws.Root], null);

        ws.Service.ReindexFromDisk(artifact);
        Assert.False(ws.Service.Index.Contains(artifact));

        // Excluded paths short-circuit rather than queueing a debounced re-index.
        var queued = ws.Service.QueueReindexAsync(artifact);
        Assert.True(queued.IsCompleted);
        await queued;
        Assert.False(ws.Service.Index.Contains(artifact));
    }

    [Fact]
    public async Task RealSourceUnderAGitIgnoredNameElsewhere_IsStillIndexed()
    {
        using var ws = NewWorkspace();
        // "dist/" is ignored at the root, but a nested source directory that merely
        // *contains* the word must not be.
        ws.WriteRootFile(".gitignore", "/dist\n");
        var ignored = ws.WriteRootFile(Path.Combine("dist", "gen.zs"), "(define g 1)");
        var kept = ws.WriteRootFile(Path.Combine("src", "distance.zs"), "(define d 1)");

        await ws.Service.InitializeWorkspaceAsync([ws.Root], null);

        Assert.False(ws.Service.Index.Contains(ignored));
        Assert.True(ws.Service.Index.Contains(kept));
    }
}
