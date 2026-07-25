using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class WorkspaceScanProgressTests
{
    /// <summary>No-logic recording fake (call recording only, per docs/MOCKS.md).</summary>
    private sealed class RecordingReporter : IWorkspaceScanReporter
    {
        public List<int> BeginCalls { get; } = [];
        public List<(int Processed, int Total, string Path)> ReportCalls { get; } = [];
        public int EndCalls { get; private set; }

        public void Begin(int totalFiles) => BeginCalls.Add(totalFiles);

        public void Report(int processedFiles, int totalFiles, string currentFilePath) =>
            ReportCalls.Add((processedFiles, totalFiles, currentFilePath));

        public void End() => EndCalls++;
    }

    private static TempPackageWorkspace NewWorkspace()
    {
        return new TempPackageWorkspace(
            "wspkg",
            new Dictionary<string, string>
            {
                ["one.zs"] = "(module one)\n(define x 1)\n(export x)",
                ["two.zs"] = "(module two)\n(define y 2)\n(export y)",
                ["broken.zs"] = "(module broken)\n(define (f",
            }
        );
    }

    [Fact]
    public async Task Scan_ReportsBeginProgressEnd()
    {
        using var ws = NewWorkspace();
        var reporter = new RecordingReporter();

        await ws.Service.InitializeWorkspaceAsync([ws.Root], reporter);

        var total = Assert.Single(reporter.BeginCalls);
        Assert.Equal(3, total);
        Assert.Equal(3, reporter.ReportCalls.Count);
        Assert.Equal(1, reporter.EndCalls);

        // Reports are monotonically increasing and carry the full path of each file.
        Assert.Equal([1, 2, 3], reporter.ReportCalls.Select(r => r.Processed));
        Assert.All(reporter.ReportCalls, r => Assert.Equal(3, r.Total));
        Assert.Contains(
            reporter.ReportCalls,
            r => string.Equals(r.Path, ws.PathOf("one.zs"), StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task Scan_EndsEvenWhenFilesFailToCompile()
    {
        using var ws = NewWorkspace();
        var reporter = new RecordingReporter();

        await ws.Service.InitializeWorkspaceAsync([ws.Root], reporter);

        // broken.zs fails to compile but the scan still finishes and reports End once.
        Assert.Equal(1, reporter.EndCalls);
        Assert.True(ws.Service.Index.Contains(ws.PathOf("one.zs")));
        Assert.True(ws.Service.Index.Contains(ws.PathOf("two.zs")));
    }

    [Fact]
    public async Task SecondInitialize_IsIdempotent_NoSecondScan()
    {
        using var ws = NewWorkspace();
        var first = new RecordingReporter();
        var second = new RecordingReporter();

        await ws.Service.InitializeWorkspaceAsync([ws.Root], first);
        await ws.Service.InitializeWorkspaceAsync([ws.Root], second);

        Assert.Single(first.BeginCalls);
        Assert.Empty(second.BeginCalls);
    }
}
