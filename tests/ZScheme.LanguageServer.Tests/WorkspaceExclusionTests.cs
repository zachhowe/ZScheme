using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

/// <summary>
///     The startup scan must not index generated trees. The motivating case: the fuzzer
///     writes thousands of <c>original.zs</c> repro dumps under the gitignored
///     <c>fuzz-runs/</c>, which used to dominate both the scan and the symbol index.
/// </summary>
/// <remarks>
///     Every claim here is about the *walk*, so it is asserted against the scan's own
///     report rather than against the index. The index is downstream of two further skips
///     — unreadable file, uncompilable file — that are silent by design, so
///     <c>Assert.True(Index.Contains(x))</c> fails for reasons that have nothing to do
///     with exclusion: it once failed under load when a sibling test's `zs-lsp` starved
///     the scan into a transient sharing lock, and read as a gitignore-anchoring bug.
///     Negative assertions keep both checks — load can only *remove* entries, so
///     "absent from the index" cannot be a load artifact — but positive ones go through
///     the reporter. That a scanned file reaches the index at all is pinned by
///     <see cref="WorkspaceScanProgressTests" />.
/// </remarks>
public sealed class WorkspaceExclusionTests
{
    /// <summary>No-logic recording fake (call recording only, per docs/MOCKS.md).</summary>
    private sealed class RecordingReporter : IWorkspaceScanReporter
    {
        public List<int> BeginCalls { get; } = [];

        /// <summary>Full paths, in scan order: what survived the exclusion rules.</summary>
        public List<string> ScannedPaths { get; } = [];

        public void Begin(int totalFiles) => BeginCalls.Add(totalFiles);

        public void Report(int processedFiles, int totalFiles, string currentFilePath) =>
            ScannedPaths.Add(currentFilePath);

        public void End() { }
    }

    private static TempPackageWorkspace NewWorkspace() =>
        new(
            "expkg",
            new Dictionary<string, string> { ["one.zs"] = "(module one)\n(define x 1)\n(export x)" }
        );

    /// <summary>Compared the way the index compares paths, so a drive-letter or casing
    ///     difference between a walked path and a test-built one cannot flake.</summary>
    private static void AssertScanned(RecordingReporter reporter, string path)
    {
        var expected = LspUri.PathOf(path);
        Assert.Contains(
            reporter.ScannedPaths,
            p => string.Equals(p, expected, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static void AssertNotScanned(RecordingReporter reporter, string path)
    {
        var expected = LspUri.PathOf(path);
        Assert.DoesNotContain(
            reporter.ScannedPaths,
            p => string.Equals(p, expected, StringComparison.OrdinalIgnoreCase)
        );
    }

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
        AssertNotScanned(reporter, artifact);
        AssertScanned(reporter, ws.PathOf("one.zs"));
        Assert.False(ws.Service.Index.Contains(artifact));
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

        AssertNotScanned(reporter, ignored);
        AssertScanned(reporter, kept);
        Assert.False(ws.Service.Index.Contains(ignored));
    }

    /// <summary>
    ///     The distinction the rest of this class now relies on: a file the walk kept but
    ///     the scan could not index — here because it does not parse, under load because
    ///     <c>File.ReadAllText</c> lost a race — is absent from the index and present in
    ///     the report. Asserting only on the index cannot tell that apart from exclusion.
    /// </summary>
    [Fact]
    public async Task UnindexableFile_IsReportedAsScannedAnyway()
    {
        using var ws = NewWorkspace();
        var broken = ws.WriteRootFile(
            Path.Combine("tools", "grammars", "broken.zs"),
            "(module broken)\n(define (f"
        );
        var reporter = new RecordingReporter();

        await ws.Service.InitializeWorkspaceAsync([ws.Root], reporter);

        AssertScanned(reporter, broken);
        Assert.False(ws.Service.Index.Contains(broken));
    }

    [Fact]
    public async Task Negation_ReIncludesAFile()
    {
        using var ws = NewWorkspace();
        ws.WriteRootFile(".gitignore", "*.gen.zs\n!keep.gen.zs\n");
        var dropped = ws.WriteRootFile("dropped.gen.zs", "(define d 1)");
        var kept = ws.WriteRootFile("keep.gen.zs", "(define k 1)");
        var reporter = new RecordingReporter();

        await ws.Service.InitializeWorkspaceAsync([ws.Root], reporter);

        AssertNotScanned(reporter, dropped);
        AssertScanned(reporter, kept);
        Assert.False(ws.Service.Index.Contains(dropped));
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
        var reporter = new RecordingReporter();

        await ws.Service.InitializeWorkspaceAsync([ws.Root], reporter);

        Assert.All(generated, path => AssertNotScanned(reporter, path));
        Assert.All(generated, path => Assert.False(ws.Service.Index.Contains(path)));
        AssertScanned(reporter, ws.PathOf("one.zs"));
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
    public async Task RealSourceUnderAGitIgnoredNameElsewhere_IsStillScanned()
    {
        using var ws = NewWorkspace();
        // "dist/" is ignored at the root, but a nested source directory that merely
        // *contains* the word must not be.
        ws.WriteRootFile(".gitignore", "/dist\n");
        var ignored = ws.WriteRootFile(Path.Combine("dist", "gen.zs"), "(define g 1)");
        var kept = ws.WriteRootFile(Path.Combine("src", "distance.zs"), "(define d 1)");
        var reporter = new RecordingReporter();

        await ws.Service.InitializeWorkspaceAsync([ws.Root], reporter);

        AssertNotScanned(reporter, ignored);
        AssertScanned(reporter, kept);
        Assert.False(ws.Service.Index.Contains(ignored));
    }
}
