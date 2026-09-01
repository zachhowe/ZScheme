using Xunit;
using ZScheme.Compiler.Cache;

namespace ZScheme.Compiler.Tests.Cache;

public sealed class AtomicDirectoryTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicDirectoryTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "zscheme-atomic-test-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>
    ///     A fill that writes only into a subdirectory never refreshes the timestamp the sweep
    ///     judges it by. The NuGet resolver stamps its staging tree once — when it creates
    ///     <c>staging/bin</c> — and every assembly it extracts lands under that, so a cold
    ///     resolve of a large graph over a slow link ages past the cutoff while it is running,
    ///     and a resolve starting beside it deletes the tree the first one is still filling.
    /// </summary>
    [Fact]
    public void TouchKeepsALongFillFromBeingSwept()
    {
        var dest = Path.Combine(_tempDir, "entry");
        var staging = AtomicDirectory.StagingPathFor(dest);
        var bin = Path.Combine(staging, "bin");
        Directory.CreateDirectory(bin);

        // As if the fill started before the cutoff and has been extracting into bin ever since.
        Directory.SetLastWriteTimeUtc(staging, DateTime.UtcNow - TimeSpan.FromHours(2));
        File.WriteAllBytes(Path.Combine(bin, "one.dll"), [0x4D, 0x5A]);
        Assert.True(
            Directory.GetLastWriteTimeUtc(staging) < DateTime.UtcNow - TimeSpan.FromHours(1)
        );

        AtomicDirectory.Touch(staging);

        // A second resolve, which sweeps before it stages a tree of its own.
        AtomicDirectory.StagingPathFor(dest);

        Assert.True(File.Exists(Path.Combine(bin, "one.dll")));
    }

    /// <summary>
    ///     The sweep itself, which is what a fill has to keep saying it is alive against: scratch
    ///     from a run that was killed part-way is invisible to readers but costs disk for good,
    ///     since nothing else walks these caches.
    /// </summary>
    [Fact]
    public void ScratchFromAnAbandonedFillIsSwept()
    {
        var dest = Path.Combine(_tempDir, "entry");
        var staging = AtomicDirectory.StagingPathFor(dest);
        Directory.CreateDirectory(Path.Combine(staging, "bin"));
        Directory.SetLastWriteTimeUtc(staging, DateTime.UtcNow - TimeSpan.FromHours(2));

        AtomicDirectory.StagingPathFor(dest);

        Assert.False(Directory.Exists(staging));
    }
}
