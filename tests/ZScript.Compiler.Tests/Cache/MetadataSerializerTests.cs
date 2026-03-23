using Xunit;
using ZScript.Compiler.Ast;
using ZScript.Compiler.Cache;
using ZScript.Compiler.Modules;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Tests.Cache;

public sealed class MetadataSerializerTests
{
    [Fact]
    public void RoundTrip_FullModuleMetadata()
    {
        var modules = new Dictionary<string, CompiledModule>
        {
            ["core"] = new CompiledModule(
                "core",
                "core.zs",
                new HashSet<string> { "id", "const" },
                new Dictionary<string, ZType>
                {
                    ["id"] = new ZType.ZForAllType([1000],
                        new ZType.ZFuncType([new ZType.ZTypeVar(1000)], new ZType.ZTypeVar(1000))),
                    ["const"] = new ZType.ZForAllType([1000, 1001],
                        new ZType.ZFuncType([new ZType.ZTypeVar(1000), new ZType.ZTypeVar(1001)],
                            new ZType.ZTypeVar(1000)))
                },
                new Dictionary<string, (string, string, int, ClrImportKind)>(),
                [],
                [],
                new Dictionary<string, MacroDefinition>()),
        };

        var json = MetadataSerializer.Serialize("zscript-stdlib", "0.1.0", "zscript-stdlib", modules);
        var result = MetadataSerializer.Deserialize(json, "/path/to/assembly.dll");

        Assert.NotNull(result);
        Assert.Equal("zscript-stdlib", result.PackageName);
        Assert.Equal("0.1.0", result.Version);
        Assert.Equal("/path/to/assembly.dll", result.AssemblyPath);
        Assert.Single(result.Modules);
        Assert.True(result.Modules.ContainsKey("core"));

        var coreMod = result.Modules["core"];
        Assert.Contains("id", coreMod.ExportedNames);
        Assert.Contains("const", coreMod.ExportedNames);
        Assert.Equal(2, coreMod.ExportedTypes.Count);
    }

    [Fact]
    public void RoundTrip_ModuleWithClrImports()
    {
        var modules = new Dictionary<string, CompiledModule>
        {
            ["list"] = new CompiledModule(
                "list",
                "list.zs",
                new HashSet<string> { "list/map", "list/fold" },
                new Dictionary<string, ZType>
                {
                    ["list/map"] = ZType.Int // simplified
                },
                new Dictionary<string, (string, string, int, ClrImportKind)>
                {
                    ["list/map"] = ("System.Collections.Immutable.ImmutableList`1", "ConvertAll", 1, ClrImportKind.Instance)
                },
                [],
                ["System.Collections.Immutable"],
                new Dictionary<string, MacroDefinition>()),
        };

        var json = MetadataSerializer.Serialize("zscript-stdlib", "0.1.0", "zscript-stdlib", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var listMod = result.Modules["list"];
        Assert.Single(listMod.ExportedClrImports);
        var (typeName, methodName, genericArity, kind) = listMod.ExportedClrImports["list/map"];
        Assert.Equal("System.Collections.Immutable.ImmutableList`1", typeName);
        Assert.Equal("ConvertAll", methodName);
        Assert.Equal(1, genericArity);
        Assert.Equal(ClrImportKind.Instance, kind);
        Assert.Contains("System.Collections.Immutable", listMod.ExportedClrNamespaces);
    }

    [Fact]
    public void RoundTrip_ModuleWithUnionAndRecordCtors()
    {
        var modules = new Dictionary<string, CompiledModule>
        {
            ["option"] = new CompiledModule(
                "option",
                "option.zs",
                new HashSet<string> { "Some", "None" },
                new Dictionary<string, ZType>(),
                new Dictionary<string, (string, string, int, ClrImportKind)>(),
                [],
                [],
                new Dictionary<string, MacroDefinition>(),
                new Dictionary<string, string> { ["Some"] = "Option", ["None"] = "Option" },
                new Dictionary<string, List<string>> { ["Point"] = ["x", "y"] }),
        };

        var json = MetadataSerializer.Serialize("test-pkg", "1.0.0", "test-pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var optMod = result.Modules["option"];
        Assert.NotNull(optMod.ExportedUnionCtors);
        Assert.Equal("Option", optMod.ExportedUnionCtors["Some"]);
        Assert.Equal("Option", optMod.ExportedUnionCtors["None"]);
        Assert.NotNull(optMod.ExportedRecordCtors);
        Assert.Equal(["x", "y"], optMod.ExportedRecordCtors["Point"]);
    }

    [Fact]
    public void Deserialize_InvalidFormatVersion_ReturnsNull()
    {
        var json = """
            {
                "formatVersion": 999,
                "package": "test",
                "version": "1.0.0",
                "modules": {}
            }
            """;
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_InvalidJson_Throws()
    {
        Assert.ThrowsAny<Exception>(() => MetadataSerializer.Deserialize("not json at all", "/assembly.dll"));
    }

    [Fact]
    public void RoundTrip_PreservesExportedTypes()
    {
        var forAllType = new ZType.ZForAllType([1000],
            new ZType.ZFuncType(
                [new ZType.ZTypeVar(1000)],
                new ZType.ZNamedType("Option", [new ZType.ZTypeVar(1000)])));

        var modules = new Dictionary<string, CompiledModule>
        {
            ["test"] = new CompiledModule(
                "test", "test.zs",
                new HashSet<string> { "wrap" },
                new Dictionary<string, ZType> { ["wrap"] = forAllType },
                new Dictionary<string, (string, string, int, ClrImportKind)>(),
                [], [],
                new Dictionary<string, MacroDefinition>()),
        };

        var json = MetadataSerializer.Serialize("pkg", "1.0.0", "pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var mod = result.Modules["test"];
        Assert.Equal(forAllType.ToString(), mod.ExportedTypes["wrap"].ToString());
    }
}
