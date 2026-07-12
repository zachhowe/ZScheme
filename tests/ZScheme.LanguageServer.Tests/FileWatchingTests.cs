using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public class FileWatchingTests
{
    private static TempPackageWorkspace CreateWorkspace()
    {
        return new TempPackageWorkspace(
            "watchpkg",
            new Dictionary<string, string>
            {
                ["util.zs"] = "(define (original-helper [x : Int]) (+ x 1))\n",
                ["main.zs"] = "(define (entry) 42)\n",
            }
        );
    }

    [Fact]
    public void ReindexFromDisk_PicksUpDiskEditOfUnopenedFile()
    {
        using var ws = CreateWorkspace();
        ws.Service.ReindexFromDisk(ws.PathOf("util.zs"));
        Assert.NotEmpty(ws.Service.Index.ResolveDefinition(null, "original-helper"));

        File.WriteAllText(ws.PathOf("util.zs"), "(define (renamed-helper [x : Int]) (+ x 2))\n");
        ws.Service.ReindexFromDisk(ws.PathOf("util.zs"));

        Assert.NotEmpty(ws.Service.Index.ResolveDefinition(null, "renamed-helper"));
        Assert.Empty(ws.Service.Index.ResolveDefinition(null, "original-helper"));
    }

    [Fact]
    public void ReindexFromDisk_IndexesNewFile()
    {
        using var ws = CreateWorkspace();
        var newPath = Path.Combine(Path.GetDirectoryName(ws.PathOf("util.zs"))!, "fresh.zs");
        File.WriteAllText(newPath, "(define (brand-new-fn) 7)\n");

        ws.Service.ReindexFromDisk(newPath);

        Assert.True(ws.Service.Index.Contains(Path.GetFullPath(newPath)));
        Assert.NotEmpty(ws.Service.Index.ResolveDefinition(null, "brand-new-fn"));
    }

    [Fact]
    public void ReindexFromDisk_RemovesSliceWhenFileGone()
    {
        using var ws = CreateWorkspace();
        var path = ws.PathOf("util.zs");
        ws.Service.ReindexFromDisk(path);
        Assert.True(ws.Service.Index.Contains(Path.GetFullPath(path)));

        File.Delete(path);
        ws.Service.ReindexFromDisk(path);

        Assert.False(ws.Service.Index.Contains(Path.GetFullPath(path)));
        Assert.Empty(ws.Service.Index.ResolveDefinition(null, "original-helper"));
    }

    [Fact]
    public void RemoveFromIndex_PurgesDefinitionsAndReferences()
    {
        using var ws = CreateWorkspace();
        var path = ws.PathOf("util.zs");
        ws.Service.ReindexFromDisk(path);
        Assert.NotEmpty(ws.Service.Index.ResolveDefinition(null, "original-helper"));

        ws.Service.RemoveFromIndex(path);

        Assert.False(ws.Service.Index.Contains(Path.GetFullPath(path)));
        Assert.Empty(ws.Service.Index.ResolveDefinition(null, "original-helper"));
    }

    [Fact]
    public void ReindexFromDisk_NoOpsWhileDocumentOpen_ThenDiskWinsAfterClose()
    {
        using var ws = CreateWorkspace();
        // Open with buffer content that differs from disk.
        ws.Service.AnalyzeImmediate(
            ws.UriOf("util.zs"),
            "(define (buffer-only-fn) 1)\n",
            1
        );
        Assert.NotEmpty(ws.Service.Index.ResolveDefinition(null, "buffer-only-fn"));

        // Conflicting disk write while the buffer is open: buffer wins.
        File.WriteAllText(ws.PathOf("util.zs"), "(define (disk-only-fn) 2)\n");
        ws.Service.ReindexFromDisk(ws.PathOf("util.zs"));
        Assert.NotEmpty(ws.Service.Index.ResolveDefinition(null, "buffer-only-fn"));
        Assert.Empty(ws.Service.Index.ResolveDefinition(null, "disk-only-fn"));

        // After close, disk wins.
        ws.Service.RemoveDocument(ws.UriOf("util.zs"));
        ws.Service.ReindexFromDisk(ws.PathOf("util.zs"));
        Assert.NotEmpty(ws.Service.Index.ResolveDefinition(null, "disk-only-fn"));
        Assert.Empty(ws.Service.Index.ResolveDefinition(null, "buffer-only-fn"));
    }

    [Fact]
    public async Task QueueReindexAsync_CoalescesRepeatedEvents()
    {
        using var ws = CreateWorkspace();
        File.WriteAllText(ws.PathOf("util.zs"), "(define (queued-fn) 3)\n");

        Task last = Task.CompletedTask;
        for (var i = 0; i < 10; i++)
            last = ws.Service.QueueReindexAsync(ws.PathOf("util.zs"));

        await last;

        Assert.NotEmpty(ws.Service.Index.ResolveDefinition(null, "queued-fn"));
        Assert.Empty(ws.Service.Index.ResolveDefinition(null, "original-helper"));
    }

    [Fact]
    public async Task Handler_RoutesCreateChangeAndDelete()
    {
        using var ws = CreateWorkspace();
        var handler = new DidChangeWatchedFilesHandler(ws.Service);

        // Change event on an unopened file.
        File.WriteAllText(ws.PathOf("util.zs"), "(define (watched-fn) 4)\n");
        await handler.Handle(
            new DidChangeWatchedFilesParams
            {
                Changes = new Container<FileEvent>(
                    new FileEvent
                    {
                        Uri = DocumentUri.FromFileSystemPath(ws.PathOf("util.zs")),
                        Type = FileChangeType.Changed,
                    }
                ),
            },
            CancellationToken.None
        );

        // The handler queues a coalesced re-index; wait past the quiet period.
        await WaitForAsync(() =>
            ws.Service.Index.ResolveDefinition(null, "watched-fn").Count > 0
        );

        // Delete event purges immediately (no debounce).
        File.Delete(ws.PathOf("util.zs"));
        await handler.Handle(
            new DidChangeWatchedFilesParams
            {
                Changes = new Container<FileEvent>(
                    new FileEvent
                    {
                        Uri = DocumentUri.FromFileSystemPath(ws.PathOf("util.zs")),
                        Type = FileChangeType.Deleted,
                    }
                ),
            },
            CancellationToken.None
        );

        Assert.Empty(ws.Service.Index.ResolveDefinition(null, "watched-fn"));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition not met within timeout");
            await Task.Delay(50);
        }
    }
}
