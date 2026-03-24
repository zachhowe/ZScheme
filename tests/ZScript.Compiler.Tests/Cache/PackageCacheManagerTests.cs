using Xunit;
using ZScript.Compiler.Ast;
using ZScript.Compiler.Cache;
using ZScript.Compiler.Modules;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Tests.Cache;

public sealed class PackageCacheManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PackageCacheManager _cache;

    public PackageCacheManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "zscript-cache-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _cache = new PackageCacheManager(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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

    private static Dictionary<string, CompiledModule> CreateTestModules()
    {
        return new Dictionary<string, CompiledModule>
        {
            ["core"] = new CompiledModule(
                "core", "core.zs",
                new HashSet<string> { "id", "const" },
                new Dictionary<string, ZType>
                {
                    ["id"] = new ZType.ZForAllType([1000],
                        new ZType.ZFuncType([new ZType.ZTypeVar(1000)], new ZType.ZTypeVar(1000))),
                    ["const"] = ZType.Int
                },
                new Dictionary<string, (string, string, int, ClrImportKind, IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [], [],
                new Dictionary<string, MacroDefinition>()),
        };
    }
}
