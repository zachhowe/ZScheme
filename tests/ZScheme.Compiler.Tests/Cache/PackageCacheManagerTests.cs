using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Cache;

public sealed class PackageCacheManagerTests : IDisposable
{
    private readonly PackageCacheManager _cache;
    private readonly string _tempDir;

    public PackageCacheManagerTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "zscheme-cache-test-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(_tempDir);
        _cache = new PackageCacheManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void TryLoad_NonExistentPackage_ReturnsNull()
    {
        var result = _cache.TryLoad("nonexistent", "1.0.0");
        Assert.Null(result);
    }

    [Fact]
    public void Store_ThenLoad_RoundTrips()
    {
        var modules = CreateTestModules();
        var assemblyBytes = new byte[] { 0x4D, 0x5A }; // Minimal PE header

        _cache.Store("test-pkg", "1.0.0", assemblyBytes, modules);
        var result = _cache.TryLoad("test-pkg", "1.0.0");

        Assert.NotNull(result);
        Assert.Equal("test-pkg", result.PackageName);
        Assert.Equal("1.0.0", result.Version);
        Assert.True(result.Modules.ContainsKey("core"));

        // Verify assembly was written
        Assert.True(File.Exists(result.AssemblyPath));
        var loadedBytes = File.ReadAllBytes(result.AssemblyPath);
        Assert.Equal(assemblyBytes, loadedBytes);
    }

    [Fact]
    public void Store_ThenLoad_PreservesModuleInfo()
    {
        var modules = CreateTestModules();
        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules);

        var result = _cache.TryLoad("test-pkg", "1.0.0");
        Assert.NotNull(result);

        var coreMod = result.Modules["core"];
        Assert.Contains("id", coreMod.ExportedNames);
        Assert.Contains("const", coreMod.ExportedNames);
        Assert.Equal(2, coreMod.ExportedTypes.Count);
    }

    [Fact]
    public void Invalidate_RemovesPackage()
    {
        var modules = CreateTestModules();
        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules);

        // Verify stored
        Assert.NotNull(_cache.TryLoad("test-pkg", "1.0.0"));

        // Invalidate
        _cache.Invalidate("test-pkg", "1.0.0");

        // Verify removed
        Assert.Null(_cache.TryLoad("test-pkg", "1.0.0"));
    }

    [Fact]
    public void Invalidate_NonExistent_DoesNotThrow()
    {
        _cache.Invalidate("nonexistent", "1.0.0"); // Should not throw
    }

    [Fact]
    public void Store_MultipleVersions_IndependentlyAccessible()
    {
        var modules1 = CreateTestModules();
        var modules2 = CreateTestModules();

        _cache.Store("test-pkg", "1.0.0", [0x01], modules1);
        _cache.Store("test-pkg", "2.0.0", [0x02], modules2);

        var v1 = _cache.TryLoad("test-pkg", "1.0.0");
        var v2 = _cache.TryLoad("test-pkg", "2.0.0");

        Assert.NotNull(v1);
        Assert.NotNull(v2);
        Assert.Equal("1.0.0", v1.Version);
        Assert.Equal("2.0.0", v2.Version);

        // Verify different assembly bytes
        var v1Bytes = File.ReadAllBytes(v1.AssemblyPath);
        var v2Bytes = File.ReadAllBytes(v2.AssemblyPath);
        Assert.Equal([0x01], v1Bytes);
        Assert.Equal([0x02], v2Bytes);
    }

    /// <summary>A payload big enough that writing it is not instantaneous, so a reader
    ///     sampling in a tight loop lands inside the write rather than around it.</summary>
    private static byte[] BigAssembly()
    {
        var body = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(body);
        return body;
    }

    /// <summary>
    ///     Regression: this cache is shared by every compile on the machine — the test assemblies
    ///     <c>dotnet test</c> runs side by side, several <c>zs build</c>s. Writing the assembly and
    ///     its metadata straight into the version directory made the entry visible while it was
    ///     still being filled, and <see cref="PackageCacheManager.TryLoad" /> reads that as a miss:
    ///     the reader re-installed the package and collided with the writer still holding the file
    ///     open. The directory must therefore never exist in a state <c>TryLoad</c> misses on.
    /// </summary>
    [Fact]
    public async Task StoreIsNeverVisibleHalfWritten()
    {
        var modules = CreateTestModules();
        var body = BigAssembly();
        var packageDir = Path.Combine(_tempDir, "test-pkg", "1.0.0");

        using var done = new CancellationTokenSource();
        var torn = 0;
        var watcher = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
                try
                {
                    // A directory that is there but does not load is the half-written entry.
                    // No directory at all is fine: the commit moves the old entry aside before
                    // renaming the new one in, and a reader in that window simply misses.
                    if (Directory.Exists(packageDir) && _cache.TryLoad("test-pkg", "1.0.0") is null)
                        Interlocked.Increment(ref torn);
                }
                catch (Exception)
                {
                    // Metadata caught mid-write: torn just the same.
                    Interlocked.Increment(ref torn);
                }
        });

        for (var i = 0; i < 3; i++)
        {
            _cache.Store("test-pkg", "1.0.0", body, modules);
            Assert.NotNull(_cache.TryLoad("test-pkg", "1.0.0"));
        }

        await done.CancelAsync();
        await watcher;

        Assert.Equal(0, Volatile.Read(ref torn));
    }

    /// <summary>
    ///     The same entry stored from several processes at once — the shape a cold cache takes on
    ///     CI, where every test assembly auto-installs the same packages. Writing in place made the
    ///     writers collide outright ("the process cannot access the file … because it is being used
    ///     by another process"); each now assembles its own copy and renames it in.
    /// </summary>
    [Fact]
    public async Task ConcurrentStoresOfOneVersionAllSucceed()
    {
        const int writers = 8;
        var modules = CreateTestModules();
        var body = BigAssembly();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stores = Enumerable
            .Range(0, writers)
            .Select(_ =>
                Task.Run(async () =>
                {
                    await start.Task;
                    // A separate manager per writer: in production these are separate processes,
                    // so nothing in-process is serializing them.
                    new PackageCacheManager(_tempDir).Store("test-pkg", "1.0.0", body, modules);
                })
            )
            .ToList();

        start.SetResult();
        await Task.WhenAll(stores);

        var result = _cache.TryLoad("test-pkg", "1.0.0");
        Assert.NotNull(result);
        Assert.Equal(body, await File.ReadAllBytesAsync(result.AssemblyPath));

        // Only the committed version directory survives: every staging tree was cleaned up.
        Assert.Equal(
            ["1.0.0"],
            Directory
                .GetDirectories(Path.Combine(_tempDir, "test-pkg"))
                .Select(Path.GetFileName)
                .Order()
        );
    }

    /// <summary>
    ///     Regression: a store that published nothing used to be silent. The commit logged its
    ///     failure and returned, so <c>zs install</c> printed "cached at ..." and exited 0 while
    ///     the version directory still held the previous build — and every later compile linked
    ///     that one. Writing straight into the version directory used to fail loudly here ("used
    ///     by another process"), and a store that cannot publish must still say so.
    /// </summary>
    [Fact]
    public void StoreThatCannotPublishThrows()
    {
        var modules = CreateTestModules();
        var packageDir = Path.Combine(_tempDir, "test-pkg", "1.0.0");
        Directory.CreateDirectory(Path.GetDirectoryName(packageDir)!);

        // A plain file where the version directory belongs: nothing can be renamed onto it. That
        // is the portable stand-in for the entry a Windows file lock pins in place.
        File.WriteAllText(packageDir, "");

        Assert.Throws<IOException>(() => _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules));
    }

    [Fact]
    public void Store_Overwrite_ReplacesExisting()
    {
        var modules = CreateTestModules();
        _cache.Store("test-pkg", "1.0.0", [0x01], modules);
        _cache.Store("test-pkg", "1.0.0", [0x02], modules);

        var result = _cache.TryLoad("test-pkg", "1.0.0");
        Assert.NotNull(result);

        var bytes = File.ReadAllBytes(result.AssemblyPath);
        Assert.Equal([0x02], bytes);
    }

    [Fact]
    public void Store_ThenLoad_PreservesImportPrefixAndDefaultModule()
    {
        var modules = CreateTestModules();
        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules, "test", "core");

        var result = _cache.TryLoad("test-pkg", "1.0.0");

        Assert.NotNull(result);
        Assert.Equal("test", result.ImportPrefix);
        Assert.Equal("core", result.DefaultModule);
    }

    [Fact]
    public void Store_ThenLoad_NullPrefixAndDefaultModule()
    {
        var modules = CreateTestModules();
        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules);

        var result = _cache.TryLoad("test-pkg", "1.0.0");

        Assert.NotNull(result);
        Assert.Null(result.ImportPrefix);
        Assert.Null(result.DefaultModule);
    }

    [Fact]
    public void TryLoadLatest_NonExistentPackage_ReturnsNull()
    {
        var result = _cache.TryLoadLatest("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public void TryLoadLatest_SingleVersion_ReturnsThatVersion()
    {
        var modules = CreateTestModules();
        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules);

        var result = _cache.TryLoadLatest("test-pkg");

        Assert.NotNull(result);
        Assert.Equal("1.0.0", result.Version);
    }

    [Fact]
    public void TryLoadLatest_MultipleVersions_ReturnsHighest()
    {
        var modules = CreateTestModules();
        _cache.Store("test-pkg", "1.0.0", [0x01], modules);
        _cache.Store("test-pkg", "2.3.0", [0x02], modules);
        _cache.Store("test-pkg", "1.5.0", [0x03], modules);

        var result = _cache.TryLoadLatest("test-pkg");

        Assert.NotNull(result);
        Assert.Equal("2.3.0", result.Version);
    }

    [Fact]
    public void TryLoadLatest_EmptyPackageDir_ReturnsNull()
    {
        // Create the package directory but don't store any versions
        Directory.CreateDirectory(Path.Combine(_tempDir, "empty-pkg"));

        var result = _cache.TryLoadLatest("empty-pkg");
        Assert.Null(result);
    }

    [Fact]
    public void NullCacheRoot_HonorsProcessDefault()
    {
        var overrideRoot = Path.Combine(
            Path.GetTempPath(),
            "zscheme-proc-default-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(overrideRoot);
        ZSchemePaths.SetProcessDefaultCacheRoot(overrideRoot);
        try
        {
            var cache = new PackageCacheManager();
            var modules = CreateTestModules();
            cache.Store("proc-default-pkg", "1.0.0", [0x4D, 0x5A], modules);

            var expectedDll = Path.Combine(
                overrideRoot,
                "pkg",
                CompilerInfo.BaseVersion,
                "proc-default-pkg",
                "1.0.0",
                "proc-default-pkg.dll"
            );
            Assert.True(File.Exists(expectedDll), $"expected DLL at {expectedDll}");
        }
        finally
        {
            ZSchemePaths.SetProcessDefaultCacheRoot(null);
            if (Directory.Exists(overrideRoot))
                Directory.Delete(overrideRoot, true);
        }
    }

    private static Dictionary<string, CompiledModule> CreateTestModules()
    {
        return new Dictionary<string, CompiledModule>
        {
            ["core"] = new(
                "core",
                "core.zs",
                new HashSet<string> { "id", "const" },
                new Dictionary<string, ZType>
                {
                    ["id"] = new ZType.ZForAllType(
                        [1000],
                        new ZType.ZFuncType([new ZType.ZTypeVar(1000)], new ZType.ZTypeVar(1000))
                    ),
                    ["const"] = ZType.Int,
                },
                new Dictionary<
                    string,
                    (
                        string,
                        string,
                        int,
                        ClrImportKind,
                        IReadOnlyDictionary<string, GenericConstraintKind>?
                    )
                >(),
                [],
                [],
                new Dictionary<string, MacroDefinition>()
            ),
        };
    }
}
