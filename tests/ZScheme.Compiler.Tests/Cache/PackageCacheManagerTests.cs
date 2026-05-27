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
        _tempDir = Path.Combine(Path.GetTempPath(), "zscheme-cache-test-" + Guid.NewGuid().ToString("N")[..8]);
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
        _cache.Store("test-pkg", "1.0.0", [0x4D, 0x5A], modules,
            "test", "core");

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
        var overrideRoot =
            Path.Combine(Path.GetTempPath(), "zscheme-proc-default-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(overrideRoot);
        ZSchemePaths.SetProcessDefaultCacheRoot(overrideRoot);
        try
        {
            var cache = new PackageCacheManager();
            var modules = CreateTestModules();
            cache.Store("proc-default-pkg", "1.0.0", [0x4D, 0x5A], modules);

            var expectedDll = Path.Combine(
                overrideRoot, "pkg", CompilerInfo.BaseVersion,
                "proc-default-pkg", "1.0.0", "proc-default-pkg.dll");
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
                "core", "core.zs",
                new HashSet<string> { "id", "const" },
                new Dictionary<string, ZType>
                {
                    ["id"] = new ZType.ZForAllType([1000],
                        new ZType.ZFuncType([new ZType.ZTypeVar(1000)], new ZType.ZTypeVar(1000))),
                    ["const"] = ZType.Int
                },
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [], [],
                new Dictionary<string, MacroDefinition>())
        };
    }
}
