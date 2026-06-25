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
            {
                // Confirmed by a second, independent enumeration before it counts. An
                // enumeration resolves the path once and then reads the directory it landed on,
                // so a commit renaming that directory aside and dropping it leaves the first
                // read describing a directory that is no longer the entry -- on Linux it comes
                // back empty, while the entry sitting at the path is whole. That view is gone
                // the moment the path is resolved again, whereas a genuinely half-written entry
                // is published for as long as it takes to write the assembly, which BigAssembly
                // makes far longer than the two reads.
                if (LooksWhole() == false && LooksWhole() == false)
                    Interlocked.Increment(ref torn);
            }

            // Whether the entry at packageDir is complete: null when there is no directory at
            // all, which is the plain miss every reader already handles. Judged on the one
            // enumeration and nothing else -- reading each file's length would stat it again,
            // and that second look is the very race being ruled out here. Any shape but the two
            // files -- one of them, or neither -- is the half-written entry this must never
            // expose. Sampling through TryLoad instead counted a read that merely raced the
            // commit: it checks both files exist and then reads the metadata, and a swap
            // landing in between makes that read throw on an entry that was never half-written.
            bool? LooksWhole()
            {
                string?[] present;
                try
                {
                    present = Directory.GetFiles(packageDir).Select(Path.GetFileName).ToArray();
                }
                catch (DirectoryNotFoundException)
                {
                    return null;
                }

                return present.Length == 2
                    && present.Contains("test-pkg.dll")
                    && present.Contains("test-pkg.metadata.json");
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
    ///     Regression: a lookup that lands inside a commit must come back as a hit, never as a
    ///     miss and never as an exception. Publishing an entry renames it in over whatever was
    ///     there, so the version directory is absent between the two renames — and <c>TryLoad</c>
    ///     checked that both files existed and then read the metadata: in that window it reported
    ///     a miss on an entry that is there, sending the caller off to recompile a cached package,
    ///     or threw <see cref="FileNotFoundException" /> when the swap landed between the check
    ///     and the read. Nothing between here and the compile catches that throw.
    /// </summary>
    [Fact]
    public async Task TryLoadRidesOutACommitLandingUnderIt()
    {
        var modules = CreateTestModules();
        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules);

        var packageDir = Path.Combine(_tempDir, "test-pkg", "1.0.0");
        var metadataJson = await File.ReadAllTextAsync(
            Path.Combine(packageDir, "test-pkg.metadata.json")
        );

        // A peer publishing the same entry over and over, which is what a shared cache looks like
        // from a reader: nothing but the swaps. Calling Store in a loop instead would spend
        // almost all of its time writing the assembly, and the window this is about would come
        // round a handful of times in the whole test rather than continuously.
        using var done = new CancellationTokenSource();
        var republisher = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                var staging = AtomicDirectory.StagingPathFor(packageDir);
                Directory.CreateDirectory(staging);
                File.WriteAllBytes(Path.Combine(staging, "test-pkg.dll"), [0x4D, 0x5A]);
                File.WriteAllText(
                    Path.Combine(staging, "test-pkg.metadata.json"),
                    metadataJson
                );
                AtomicDirectory.Commit(staging, packageDir);
                AtomicDirectory.TryDelete(staging);
            }
        });

        // A separate manager per lookup, as in production: nothing in-process serializes a reader
        // against the republisher. An exception escaping either fails the test.
        var hits = 0;
        for (var i = 0; i < 2000; i++)
            if (new PackageCacheManager(_tempDir).TryLoad("test-pkg", "1.0.0") is not null)
                hits++;

        await done.CancelAsync();
        await republisher;

        // Every lookup found the entry, bar the occasional one where five attempts in a row --
        // 100ms of them -- all landed inside a swap. Nothing in production republishes an entry
        // back to back like this: a real commit is separated from the next by the seconds it
        // takes to compile the package being cached. Measured against this republisher, a single
        // attempt misses roughly one lookup in seven (1715 to 1728 hits of 2000), and without
        // the guarded read it does not get this far at all -- FileNotFoundException within the
        // first few hundred lookups, every run.
        Assert.InRange(hits, 1990, 2000);
    }

    /// <summary>
    ///     The same entry stored from several processes at once — the shape a cold cache takes on
    ///     CI, where every test assembly auto-installs the same packages. Writing in place made the
    ///     writers collide outright ("the process cannot access the file … because it is being used
    ///     by another process"); each now assembles its own copy and renames it in.
    ///     <para>
    ///         All but one of them lose that race, which is a store of this build only for the
    ///         caller this models: an auto-install fills a lookup that missed, so a peer's entry
    ///         for the version is what a hit would have handed back.
    ///     </para>
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
                    new PackageCacheManager(_tempDir).Store(
                        "test-pkg",
                        "1.0.0",
                        body,
                        modules,
                        requirement: StoreRequirement.AnyBuildOfThisVersion
                    );
                })
            )
            .ToList();

        start.SetResult();
        await Task.WhenAll(stores);

        var result = _cache.TryLoad("test-pkg", "1.0.0");
        Assert.NotNull(result);
        Assert.Equal(body, await File.ReadAllBytesAsync(result.AssemblyPath));

        // Only the committed version directory is a cache entry. Scratch is dot-prefixed, and
        // requiring none of it to survive is stricter than what a commit guarantees: putting back
        // an entry it displaced is best-effort, so a writer that loses that race leaves one
        // behind by design, and the age-gated sweep is what reclaims it.
        Assert.Equal(
            ["1.0.0"],
            Directory
                .GetDirectories(Path.Combine(_tempDir, "test-pkg"))
                .Select(Path.GetFileName)
                .Where(name => name is not null && !name.StartsWith('.'))
                .Order()
        );
    }

    /// <summary>
    ///     Regression: the caller that has just cached a package needs the entry it published,
    ///     and it used to look the package up again to get it. That lookup is a lookup like any
    ///     other — a peer's commit lands in it — so the auto-installer could report "package not
    ///     found" for a package it had compiled and cached a moment earlier. A store hands back
    ///     what it wrote.
    /// </summary>
    [Fact]
    public void StoreHandsBackTheEntryItPublished()
    {
        var modules = CreateTestModules();

        var stored = _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules, "test", "core");

        Assert.NotNull(stored);

        // Indistinguishable from what a later hit hands back: the assembly it names is the one
        // in the cache, and the metadata is the metadata that was written beside it.
        var loaded = _cache.TryLoad("test-pkg", "1.0.0");
        Assert.NotNull(loaded);
        Assert.Equal(loaded.AssemblyPath, stored.AssemblyPath);
        Assert.Equal(loaded.PackageName, stored.PackageName);
        Assert.Equal(loaded.Version, stored.Version);
        Assert.Equal(loaded.ImportPrefix, stored.ImportPrefix);
        Assert.Equal(loaded.DefaultModule, stored.DefaultModule);
        Assert.Equal(loaded.Modules.Keys.Order(), stored.Modules.Keys.Order());
        Assert.True(File.Exists(stored.AssemblyPath));
    }

    /// <summary>
    ///     Regression: <see cref="CommitResult.PeerWon" /> — a peer publishing its own build under
    ///     this name and version — was the one outcome a store did not check. The version
    ///     directory is a name, not a content hash, so nothing says the two writers built the
    ///     same thing: a developer's <c>zs install</c> of an edited package could lose the race to
    ///     a compile auto-installing the pristine one at the same version, print "cached at …",
    ///     exit 0, and leave every later compile linking the other build. What the outcome is
    ///     worth depends on what the caller was after, which is why it is judged here and not in
    ///     AtomicDirectory.
    /// </summary>
    [Fact]
    public void PublishFailureJudgesACommitByWhatTheCallerNeeded()
    {
        var packageDir = Path.Combine(_tempDir, "test-pkg", "1.0.0");

        (CommitResult Commit, StoreRequirement Requirement, bool Stored)[] cases =
        [
            // The staged build is the entry: whatever the caller was after, it has it.
            (CommitResult.Committed, StoreRequirement.ThisBuild, true),
            (CommitResult.Committed, StoreRequirement.AnyBuildOfThisVersion, true),
            // A peer's entry is a build of this version and nothing more — what an auto-install
            // came for, and not what `zs install` was asked to publish.
            (CommitResult.PeerWon, StoreRequirement.ThisBuild, false),
            (CommitResult.PeerWon, StoreRequirement.AnyBuildOfThisVersion, true),
            // Nothing published, and nothing at the version directory either — this cache root
            // is empty. What a caller can do with an entry it could not displace depends on
            // there being one; see BlockedByAnEntryIsAStoreForACallerThatNeedsAnyBuild.
            (CommitResult.Blocked, StoreRequirement.ThisBuild, false),
            (CommitResult.Blocked, StoreRequirement.AnyBuildOfThisVersion, false),
            (CommitResult.Failed, StoreRequirement.ThisBuild, false),
            (CommitResult.Failed, StoreRequirement.AnyBuildOfThisVersion, false),
        ];

        foreach (var (commit, requirement, stored) in cases)
        {
            var failure = PackageCacheManager.PublishFailure(
                commit,
                requirement,
                "test-pkg",
                "1.0.0",
                packageDir
            );

            Assert.True(
                stored == (failure is null),
                $"{commit} for a caller needing {requirement}: {failure ?? "reported as stored"}"
            );
        }
    }

    /// <summary>
    ///     A commit blocked by an entry it could not displace leaves that entry in place, and for
    ///     a caller that needs a build of this version and no more, that entry is what its own
    ///     lookup would have hit had the peer published a moment earlier — it compiled because
    ///     the lookup missed, and the entry appeared while it was compiling. Failing a compile
    ///     over a usable entry buys nothing: on Windows the handle in the way is as often a
    ///     scanner reading a freshly written .dll as it is a process with the entry loaded.
    /// </summary>
    [Fact]
    public void BlockedByAnEntryIsAStoreForACallerThatNeedsAnyBuild()
    {
        var modules = CreateTestModules();
        var packageDir = Path.Combine(_tempDir, "test-pkg", "1.0.0");

        // Stand-in for the peer's entry that the blocked rename could not displace.
        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules);

        Assert.Null(Blocked(StoreRequirement.AnyBuildOfThisVersion));

        // `zs install` was asked to publish the build it was handed, and that entry is not it.
        Assert.NotNull(Blocked(StoreRequirement.ThisBuild));

        // Half an entry is not one, whatever the caller needed: a lookup misses on it.
        File.Delete(Path.Combine(packageDir, "test-pkg.metadata.json"));
        Assert.NotNull(Blocked(StoreRequirement.AnyBuildOfThisVersion));

        string? Blocked(StoreRequirement requirement) =>
            PackageCacheManager.PublishFailure(
                CommitResult.Blocked,
                requirement,
                "test-pkg",
                "1.0.0",
                packageDir
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

    /// <summary>
    ///     A store killed part-way through, or a commit that lost the race to put back the entry
    ///     it displaced, leaves its scratch directory behind. Nothing else walks the package
    ///     cache, so without a sweep these accumulate under <c>~/.zscheme</c> for good.
    /// </summary>
    [Fact]
    public void StoreSweepsScratchAnEarlierRunLeftBehind()
    {
        var modules = CreateTestModules();
        var packageRoot = Path.Combine(_tempDir, "test-pkg");
        Directory.CreateDirectory(packageRoot);

        var abandonedFill = Path.Combine(packageRoot, ".staging-abandoned");
        var orphanedEntry = Path.Combine(packageRoot, ".previous-orphaned");
        var anotherWritersFill = Path.Combine(packageRoot, ".staging-inflight");
        foreach (var dir in new[] { abandonedFill, orphanedEntry, anotherWritersFill })
            Directory.CreateDirectory(dir);

        var pastTheCutoff = DateTime.UtcNow - TimeSpan.FromHours(2);
        Directory.SetLastWriteTimeUtc(abandonedFill, pastTheCutoff);
        Directory.SetLastWriteTimeUtc(orphanedEntry, pastTheCutoff);

        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules);

        Assert.False(Directory.Exists(abandonedFill));
        Assert.False(Directory.Exists(orphanedEntry));

        // Recent scratch belongs to a writer that is still filling it: deleting that leaves the
        // other process renaming a path that is gone.
        Assert.True(Directory.Exists(anotherWritersFill));
        Assert.NotNull(_cache.TryLoad("test-pkg", "1.0.0"));
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
    public void Store_BundlesSourceFiles_StripsImportPrefix()
    {
        // In real usage modules are keyed by qualified name (e.g. "test/core") — match that.
        var modules = CreateTestModules("test/core");
        var sources = new Dictionary<string, string>
        {
            ["test/core"] = "(module test/core)\n(define id (lambda (x) x))",
            ["test/extra"] = "(module test/extra)\n(define foo 1)",
        };

        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules, "test", "core", sources);

        var pkgDir = Path.Combine(_tempDir, "test-pkg", "1.0.0");
        // Files are written relative to src/ with the import-prefix stripped.
        Assert.True(File.Exists(Path.Combine(pkgDir, "src", "core.zs")));
        Assert.True(File.Exists(Path.Combine(pkgDir, "src", "extra.zs")));

        var loaded = _cache.TryLoad("test-pkg", "1.0.0");
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.ModuleSourcePaths);
        Assert.True(loaded.ModuleSourcePaths.ContainsKey("test/core"));
        Assert.True(File.Exists(loaded.ModuleSourcePaths["test/core"]));
        Assert.Equal(pkgDir, loaded.PackageDir);
    }

    [Fact]
    public void Store_WithoutSources_LeavesModuleSourcePathsNull()
    {
        var modules = CreateTestModules();
        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules, "test", "core");

        var loaded = _cache.TryLoad("test-pkg", "1.0.0");
        Assert.NotNull(loaded);
        Assert.Null(loaded.ModuleSourcePaths);
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

    private static Dictionary<string, CompiledModule> CreateTestModules(string moduleKey = "core")
    {
        return new Dictionary<string, CompiledModule>
        {
            [moduleKey] = new(
                moduleKey,
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
