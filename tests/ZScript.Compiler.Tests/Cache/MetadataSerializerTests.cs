using Xunit;
using ZScript.Compiler.Ast;
using ZScript.Compiler.Cache;
using ZScript.Compiler.Diagnostics;
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
                new Dictionary<string, (string, string, int, ClrImportKind, IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
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
                new Dictionary<string, (string, string, int, ClrImportKind, IReadOnlyDictionary<string, GenericConstraintKind>?)>
                {
                    ["list/map"] = ("System.Collections.Immutable.ImmutableList`1", "ConvertAll", 1, ClrImportKind.Instance, null)
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
        var (typeName, methodName, genericArity, kind, constraints) = listMod.ExportedClrImports["list/map"];
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
                new Dictionary<string, (string, string, int, ClrImportKind, IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
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
    public void RoundTrip_ModuleWithExportedMacros()
    {
        var span = SourceSpan.None;
        var macros = new Dictionary<string, MacroDefinition>
        {
            ["test-case"] = new MacroDefinition(
                "test-case",
                [],
                [
                    new MacroRule(
                        new MacroPattern.PatList([
                            new MacroPattern.Literal("test-case", span),
                            new MacroPattern.Variable("name", span),
                            new MacroPattern.Ellipsis(new MacroPattern.Variable("body", span), span),
                        ], span),
                        new MacroTemplate.TList([
                            new MacroTemplate.Datum(
                                new SExpr.Atom(new Token(TokenKind.Symbol, "begin", span)), span),
                            new MacroTemplate.Ellipsis(
                                new MacroTemplate.Variable("body", span), span),
                        ], span),
                        span),
                ],
                span),
        };

        var modules = new Dictionary<string, CompiledModule>
        {
            ["zunit"] = new CompiledModule(
                "zunit",
                "zunit.zs",
                new HashSet<string> { "test-case" },
                new Dictionary<string, ZType>(),
                new Dictionary<string, (string, string, int, ClrImportKind, IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [],
                [],
                macros),
        };

        var json = MetadataSerializer.Serialize("zunit-pkg", "1.0.0", "zunit-pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var mod = result.Modules["zunit"];
        Assert.NotNull(mod.ExportedMacros);
        Assert.Single(mod.ExportedMacros);
        Assert.True(mod.ExportedMacros.ContainsKey("test-case"));

        var macro = mod.ExportedMacros["test-case"];
        Assert.Equal("test-case", macro.Name);
        Assert.Single(macro.Rules);

        var pat = Assert.IsType<MacroPattern.PatList>(macro.Rules[0].Pattern);
        Assert.Equal(3, pat.Elements.Count);
        Assert.IsType<MacroPattern.Ellipsis>(pat.Elements[2]);

        var tmpl = Assert.IsType<MacroTemplate.TList>(macro.Rules[0].Template);
        Assert.Equal(2, tmpl.Elements.Count);
        Assert.IsType<MacroTemplate.Ellipsis>(tmpl.Elements[1]);
    }

    [Fact]
    public void RoundTrip_ModuleWithNoMacros_HasNullExportedMacros()
    {
        var modules = new Dictionary<string, CompiledModule>
        {
            ["simple"] = new CompiledModule(
                "simple",
                "simple.zs",
                new HashSet<string> { "x" },
                new Dictionary<string, ZType> { ["x"] = ZType.Int },
                new Dictionary<string, (string, string, int, ClrImportKind, IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [], [],
                new Dictionary<string, MacroDefinition>()),
        };

        var json = MetadataSerializer.Serialize("pkg", "1.0.0", "pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var mod = result.Modules["simple"];
        Assert.Null(mod.ExportedMacros);
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
                new Dictionary<string, (string, string, int, ClrImportKind, IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
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
