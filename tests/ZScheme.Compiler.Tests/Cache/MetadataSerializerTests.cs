using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Modules;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Cache;

public sealed class MetadataSerializerTests
{
    [Fact]
    public void RoundTrip_FullModuleMetadata()
    {
        var modules = new Dictionary<string, CompiledModule>
        {
            ["core"] = new(
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
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [],
                [],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("zscheme-stdlib", "0.1.0", "zscheme-stdlib", modules);
        var result = MetadataSerializer.Deserialize(json, "/path/to/assembly.dll");

        Assert.NotNull(result);
        Assert.Equal("zscheme-stdlib", result.PackageName);
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
            ["list"] = new(
                "list",
                "list.zs",
                new HashSet<string> { "list/map", "list/fold" },
                new Dictionary<string, ZType>
                {
                    ["list/map"] = ZType.Int // simplified
                },
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>
                {
                    ["list/map"] = ("System.Collections.Immutable.ImmutableList`1", "ConvertAll", 1,
                        ClrImportKind.Instance, null)
                },
                [],
                ["System.Collections.Immutable"],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("zscheme-stdlib", "0.1.0", "zscheme-stdlib", modules);
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
            ["option"] = new(
                "option",
                "option.zs",
                new HashSet<string> { "Some", "None" },
                new Dictionary<string, ZType>(),
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [],
                [],
                new Dictionary<string, MacroDefinition>(),
                new Dictionary<string, string> { ["Some"] = "Option", ["None"] = "Option" },
                new Dictionary<string, List<string>> { ["Point"] = ["x", "y"] })
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
            ["test-case"] = new(
                "test-case",
                [],
                [
                    new MacroRule(
                        new MacroPattern.PatList([
                            new MacroPattern.Literal("test-case", span),
                            new MacroPattern.Variable("name", span),
                            new MacroPattern.Ellipsis(new MacroPattern.Variable("body", span), span)
                        ], span),
                        new MacroTemplate.TList([
                            new MacroTemplate.Datum(
                                new SExpr.Atom(new Token(TokenKind.Symbol, "begin", span)), span),
                            new MacroTemplate.Ellipsis(
                                new MacroTemplate.Variable("body", span), span)
                        ], span),
                        span)
                ],
                span)
        };

        var modules = new Dictionary<string, CompiledModule>
        {
            ["zunit"] = new(
                "zunit",
                "zunit.zs",
                new HashSet<string> { "test-case" },
                new Dictionary<string, ZType>(),
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [],
                [],
                macros)
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
    public void RoundTrip_ModuleWithNoMacros_HasEmptyExportedMacros()
    {
        var modules = new Dictionary<string, CompiledModule>
        {
            ["simple"] = new(
                "simple",
                "simple.zs",
                new HashSet<string> { "x" },
                new Dictionary<string, ZType> { ["x"] = ZType.Int },
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [], [],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("pkg", "1.0.0", "pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var mod = result.Modules["simple"];
        Assert.Empty(mod.ExportedMacros);
    }

    [Fact]
    public void RoundTrip_PreservesImportPrefixAndDefaultModule()
    {
        var modules = new Dictionary<string, CompiledModule>
        {
            ["zunit/zunit"] = new(
                "zunit/zunit",
                "zunit.zs",
                new HashSet<string> { "check-equal?" },
                new Dictionary<string, ZType> { ["check-equal?"] = ZType.Unit },
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [], [],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("zscheme-zunit", "0.1.0", "zscheme-zunit", modules,
            "zunit", "zunit");
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        Assert.Equal("zunit", result.ImportPrefix);
        Assert.Equal("zunit", result.DefaultModule);
    }

    [Fact]
    public void RoundTrip_OmittedPrefixAndDefaultModule_ReturnsNull()
    {
        var modules = new Dictionary<string, CompiledModule>
        {
            ["simple"] = new(
                "simple",
                "simple.zs",
                new HashSet<string> { "x" },
                new Dictionary<string, ZType> { ["x"] = ZType.Int },
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [], [],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("pkg", "1.0.0", "pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        Assert.Null(result.ImportPrefix);
        Assert.Null(result.DefaultModule);
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
            ["test"] = new(
                "test", "test.zs",
                new HashSet<string> { "wrap" },
                new Dictionary<string, ZType> { ["wrap"] = forAllType },
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [], [],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("pkg", "1.0.0", "pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var mod = result.Modules["test"];
        Assert.Equal(forAllType.ToString(), mod.ExportedTypes["wrap"].ToString());
    }

    [Fact]
    public void RoundTrip_SerializeRecordDecl_SimpleFields()
    {
        var recordDecl = new IrNode.RecordDecl(
            "Point", [],
            [new IrField("x", ZType.Int), new IrField("y", ZType.Int)]);

        var modules = new Dictionary<string, CompiledModule>
        {
            ["geom"] = new(
                "geom", "geom.zs",
                new HashSet<string>(),
                new Dictionary<string, ZType>(),
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [recordDecl], [],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("pkg", "1.0.0", "pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var mod = result.Modules["geom"];
        Assert.NotNull(mod.ExportedIrDefinitions);
        Assert.Single(mod.ExportedIrDefinitions);
        var rec = Assert.IsType<IrNode.RecordDecl>(mod.ExportedIrDefinitions[0]);
        Assert.Equal("Point", rec.Name);
        Assert.Empty(rec.TypeParams);
        Assert.Equal(2, rec.Fields.Count);
        Assert.Equal("x", rec.Fields[0].Name);
        Assert.Equal("y", rec.Fields[1].Name);
        Assert.Equal(ZType.Int.ToString(), rec.Fields[0].Type.ToString());
        Assert.Equal(ZType.Int.ToString(), rec.Fields[1].Type.ToString());
    }

    [Fact]
    public void RoundTrip_SerializeRecordDecl_WithTypeParams()
    {
        var recordDecl = new IrNode.RecordDecl(
            "Pair", ["a", "b"],
            [
                new IrField("first", new ZType.ZNamedType("a", [])),
                new IrField("second", new ZType.ZNamedType("b", []))
            ]);

        var modules = new Dictionary<string, CompiledModule>
        {
            ["data"] = new(
                "data", "data.zs",
                new HashSet<string>(),
                new Dictionary<string, ZType>(),
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [recordDecl], [],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("pkg", "1.0.0", "pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var mod = result.Modules["data"];
        Assert.NotNull(mod.ExportedIrDefinitions);
        var rec = Assert.IsType<IrNode.RecordDecl>(mod.ExportedIrDefinitions[0]);
        Assert.Equal("Pair", rec.Name);
        Assert.Equal(["a", "b"], rec.TypeParams);
        Assert.Equal("first", rec.Fields[0].Name);
        Assert.Equal("second", rec.Fields[1].Name);
        Assert.Equal(new ZType.ZNamedType("a", []).ToString(), rec.Fields[0].Type.ToString());
        Assert.Equal(new ZType.ZNamedType("b", []).ToString(), rec.Fields[1].Type.ToString());
    }

    [Fact]
    public void RoundTrip_SerializeUnionDecl_SimpleCases()
    {
        var unionDecl = new IrNode.UnionDecl(
            "Color", [],
            [
                new IrUnionCase("Red", []),
                new IrUnionCase("Green", []),
                new IrUnionCase("Blue", [])
            ]);

        var modules = new Dictionary<string, CompiledModule>
        {
            ["colors"] = new(
                "colors", "colors.zs",
                new HashSet<string>(),
                new Dictionary<string, ZType>(),
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [unionDecl], [],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("pkg", "1.0.0", "pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var mod = result.Modules["colors"];
        Assert.NotNull(mod.ExportedIrDefinitions);
        Assert.Single(mod.ExportedIrDefinitions);
        var union = Assert.IsType<IrNode.UnionDecl>(mod.ExportedIrDefinitions[0]);
        Assert.Equal("Color", union.Name);
        Assert.Empty(union.TypeParams);
        Assert.Equal(3, union.Cases.Count);
        Assert.Equal("Red", union.Cases[0].Name);
        Assert.Equal("Green", union.Cases[1].Name);
        Assert.Equal("Blue", union.Cases[2].Name);
        Assert.All(union.Cases, c => Assert.Empty(c.Fields));
    }

    [Fact]
    public void RoundTrip_SerializeUnionDecl_WithFieldsAndTypeParams()
    {
        var unionDecl = new IrNode.UnionDecl(
            "Option", ["a"],
            [
                new IrUnionCase("Some", [new IrField("value", new ZType.ZNamedType("a", []))]),
                new IrUnionCase("None", [])
            ]);

        var modules = new Dictionary<string, CompiledModule>
        {
            ["option"] = new(
                "option", "option.zs",
                new HashSet<string>(),
                new Dictionary<string, ZType>(),
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [unionDecl], [],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("pkg", "1.0.0", "pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var mod = result.Modules["option"];
        Assert.NotNull(mod.ExportedIrDefinitions);
        var union = Assert.IsType<IrNode.UnionDecl>(mod.ExportedIrDefinitions[0]);
        Assert.Equal("Option", union.Name);
        Assert.Equal(["a"], union.TypeParams);
        Assert.Equal(2, union.Cases.Count);
        Assert.Equal("Some", union.Cases[0].Name);
        Assert.Single(union.Cases[0].Fields);
        Assert.Equal("value", union.Cases[0].Fields[0].Name);
        Assert.Equal(new ZType.ZNamedType("a", []).ToString(), union.Cases[0].Fields[0].Type.ToString());
        Assert.Equal("None", union.Cases[1].Name);
        Assert.Empty(union.Cases[1].Fields);
    }

    [Fact]
    public void RoundTrip_MixedExportedIrDefinitions()
    {
        var unionDecl = new IrNode.UnionDecl(
            "Shape", [],
            [
                new IrUnionCase("Circle", [new IrField("radius", ZType.Float)]),
                new IrUnionCase("Rect", [new IrField("w", ZType.Float), new IrField("h", ZType.Float)])
            ]);

        var recordDecl = new IrNode.RecordDecl(
            "Point", [], [new IrField("x", ZType.Int), new IrField("y", ZType.Int)]);

        var modules = new Dictionary<string, CompiledModule>
        {
            ["geom"] = new(
                "geom", "geom.zs",
                new HashSet<string>(),
                new Dictionary<string, ZType>(),
                new Dictionary<string, (string, string, int, ClrImportKind,
                    IReadOnlyDictionary<string, GenericConstraintKind>?)>(),
                [unionDecl, recordDecl], [],
                new Dictionary<string, MacroDefinition>())
        };

        var json = MetadataSerializer.Serialize("pkg", "1.0.0", "pkg", modules);
        var result = MetadataSerializer.Deserialize(json, "/assembly.dll");

        Assert.NotNull(result);
        var mod = result.Modules["geom"];
        Assert.NotNull(mod.ExportedIrDefinitions);
        Assert.Equal(2, mod.ExportedIrDefinitions.Count);
        Assert.IsType<IrNode.UnionDecl>(mod.ExportedIrDefinitions[0]);
        Assert.IsType<IrNode.RecordDecl>(mod.ExportedIrDefinitions[1]);

        var union = (IrNode.UnionDecl)mod.ExportedIrDefinitions[0];
        Assert.Equal("Shape", union.Name);
        Assert.Equal(2, union.Cases.Count);

        var rec = (IrNode.RecordDecl)mod.ExportedIrDefinitions[1];
        Assert.Equal("Point", rec.Name);
        Assert.Equal(2, rec.Fields.Count);
    }
}
