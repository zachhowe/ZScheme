using Xunit;
using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Tests.Codegen;

public class AsmResolverEmitterTests
{
    private static readonly IReadOnlyList<(string ClassName, IReadOnlyList<IrNode> Definitions)> StdlibModules =
    [
        ("OptionModule", [
            new IrNode.UnionDecl("Option", ["a"], [
                new IrUnionCase("Some", [new IrField("value", new ZType.ZNamedType("a", []))]),
                new IrUnionCase("None", [])
            ])
        ]),
        ("ResultModule", [
            new IrNode.UnionDecl("Result", ["a", "e"], [
                new IrUnionCase("Ok", [new IrField("value", new ZType.ZNamedType("a", []))]),
                new IrUnionCase("Err", [new IrField("error", new ZType.ZNamedType("e", []))])
            ])
        ]),
        ("ErrorModule", [
            new IrNode.RecordDecl("ErrorInfo", [], [
                new IrField("message", ZType.String),
                new IrField("cause", new ZType.ZNamedType("Option", [new ZType.ZNamedType("ErrorInfo", [])]))
            ])
        ])
    ];

    [Fact]
    public void EmitSimpleAddFunction()
    {
        var func = new IrNode.FuncDef(
                "Add",
                [new IrParam("x", ZType.Int), new IrParam("y", ZType.Int)],
                ZType.Int,
                new IrNode.BinOp("+",
                        new IrNode.Var("x") { Type = ZType.Int },
                        new IrNode.Var("y") { Type = ZType.Int })
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitIfExpression()
    {
        var body = new IrNode.If(
                new IrNode.BinOp("=",
                        new IrNode.Var("x") { Type = ZType.Int },
                        new IrNode.IntConst(0) { Type = ZType.Int })
                    { Type = ZType.Bool },
                new IrNode.IntConst(1) { Type = ZType.Int },
                new IrNode.IntConst(0) { Type = ZType.Int })
            { Type = ZType.Int };

        var func = new IrNode.FuncDef("IsZero",
                [new IrParam("x", ZType.Int)],
                ZType.Int, body, false)
            { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitStringConstant()
    {
        var func = new IrNode.FuncDef("Greet",
                [],
                ZType.String,
                new IrNode.StringConst("hello") { Type = ZType.String },
                false)
            { Type = new ZType.ZFuncType([], ZType.String) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitCallToUserDefinedFunction()
    {
        var addFunc = new IrNode.FuncDef(
                "add",
                [new IrParam("x", ZType.Int), new IrParam("y", ZType.Int)],
                ZType.Int,
                new IrNode.BinOp("+",
                        new IrNode.Var("x") { Type = ZType.Int },
                        new IrNode.Var("y") { Type = ZType.Int })
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int) };

        var callAdd = new IrNode.Call(
                new IrNode.Var("add") { Type = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int) },
                [new IrNode.IntConst(1) { Type = ZType.Int }, new IrNode.IntConst(2) { Type = ZType.Int }])
            { Type = ZType.Int };

        var mainFunc = new IrNode.FuncDef("Main",
                [],
                ZType.Int, callAdd, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([addFunc, mainFunc]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitLetBinding()
    {
        var body = new IrNode.Let("x",
            new IrNode.IntConst(42) { Type = ZType.Int },
            new IrNode.Var("x") { Type = ZType.Int })
        { Type = ZType.Int };

        var func = new IrNode.FuncDef("GetFortyTwo",
                [],
                ZType.Int, body, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitRecordDecl()
    {
        var record = new IrNode.RecordDecl("Point", [], [
            new IrField("x", ZType.Int),
            new IrField("y", ZType.Int)
        ]);

        var seq = new IrNode.Seq([record]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnionDecl()
    {
        var union = new IrNode.UnionDecl("Shape", [], [
            new IrUnionCase("Circle", [new IrField("radius", ZType.Int)]),
            new IrUnionCase("Rect", [new IrField("w", ZType.Int), new IrField("h", ZType.Int)])
        ]);

        var seq = new IrNode.Seq([union]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitBoolOperations()
    {
        var body = new IrNode.BinOp("and",
                new IrNode.BoolConst(true) { Type = ZType.Bool },
                new IrNode.BoolConst(false) { Type = ZType.Bool })
            { Type = ZType.Bool };

        var func = new IrNode.FuncDef("AndOp",
                [],
                ZType.Bool, body, false)
            { Type = new ZType.ZFuncType([], ZType.Bool) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitWithImportedModules()
    {
        var func = new IrNode.FuncDef(
                "Identity",
                [new IrParam("x", ZType.Int)],
                ZType.Int,
                new IrNode.Var("x") { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass",
            importedModules: StdlibModules);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitRecordNewAndFieldGet()
    {
        var recordDecl = new IrNode.RecordDecl("Point", [],
        [
            new IrField("x", ZType.Int),
            new IrField("y", ZType.Int)
        ]);

        var pointType = new ZType.ZNamedType("Point", []);
        var func = new IrNode.FuncDef("test", [], ZType.Int,
                new IrNode.FieldGet(
                    new IrNode.RecordNew("Point",
                    [
                        ("x", new IrNode.IntConst(10) { Type = ZType.Int }),
                        ("y", new IrNode.IntConst(20) { Type = ZType.Int })
                    ]) { Type = pointType },
                    "x") { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([recordDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnionCaseNewAndMatch()
    {
        var unionDecl = new IrNode.UnionDecl("Shape", [],
        [
            new IrUnionCase("Circle", [new IrField("radius", ZType.Int)]),
            new IrUnionCase("Rect", [new IrField("w", ZType.Int), new IrField("h", ZType.Int)])
        ]);

        var shapeType = new ZType.ZNamedType("Shape", []);
        var func = new IrNode.FuncDef("test", [], ZType.Int,
                new IrNode.Match(
                    new IrNode.UnionCaseNew("Shape", "Circle",
                        [new IrNode.IntConst(5) { Type = ZType.Int }]) { Type = shapeType },
                    [
                        new IrMatchArm(
                            new IrPattern.Constructor("Circle", [new IrPattern.Variable("r")]),
                            new IrNode.Var("r") { Type = ZType.Int }),
                        new IrMatchArm(
                            new IrPattern.Wildcard(),
                            new IrNode.IntConst(0) { Type = ZType.Int })
                    ]) { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([unionDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClrCall()
    {
        var clrCall = new IrNode.ClrCall(
                "System.Console", "WriteLine",
                [new IrNode.StringConst("hello") { Type = ZType.String }])
            { Type = ZType.Unit };

        var seq = new IrNode.Seq([clrCall]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitStringConcat()
    {
        var func = new IrNode.FuncDef("test",
                [new IrParam("a", ZType.String), new IrParam("b", ZType.String)],
                ZType.String,
                new IrNode.BinOp("+",
                        new IrNode.Var("a") { Type = ZType.String },
                        new IrNode.Var("b") { Type = ZType.String })
                    { Type = ZType.String },
                false)
            { Type = new ZType.ZFuncType([ZType.String, ZType.String], ZType.String) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnaryNot()
    {
        var func = new IrNode.FuncDef("negate",
                [new IrParam("x", ZType.Bool)],
                ZType.Bool,
                new IrNode.UnaryOp("not",
                        new IrNode.Var("x") { Type = ZType.Bool })
                    { Type = ZType.Bool },
                false)
            { Type = new ZType.ZFuncType([ZType.Bool], ZType.Bool) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void HasEntryPointFalseForLibrary()
    {
        var func = new IrNode.FuncDef("helper", [], ZType.Int,
                new IrNode.IntConst(1) { Type = ZType.Int }, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        emitter.Emit(seq);

        Assert.False(emitter.HasEntryPoint);
    }

    [Fact]
    public void EmitTopLevelLetBinding()
    {
        var let = new IrNode.Let("x",
                new IrNode.IntConst(42) { Type = ZType.Int },
                new IrNode.UnitConst { Type = ZType.Unit })
            { Type = ZType.Unit };

        var func = new IrNode.FuncDef("getX", [], ZType.Int,
                new IrNode.Var("x") { Type = ZType.Int }, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([let, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitFloatConst()
    {
        var func = new IrNode.FuncDef("pi", [], ZType.Float,
                new IrNode.FloatConst(3.14f) { Type = ZType.Float }, false)
            { Type = new ZType.ZFuncType([], ZType.Float) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Theory]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("!=")]
    [InlineData("<=")]
    [InlineData(">=")]
    public void EmitComparisonOperators(string op)
    {
        var func = new IrNode.FuncDef("cmp",
                [new IrParam("x", ZType.Int), new IrParam("y", ZType.Int)],
                ZType.Bool,
                new IrNode.BinOp(op,
                        new IrNode.Var("x") { Type = ZType.Int },
                        new IrNode.Var("y") { Type = ZType.Int })
                    { Type = ZType.Bool },
                false)
            { Type = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Bool) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitTryCatch()
    {
        var tryCatch = new IrNode.TryCatch(
            new IrNode.IntConst(42) { Type = ZType.Int })
        {
            Type = new ZType.ZNamedType("Result", [ZType.Int, new ZType.ZNamedType("ErrorInfo", [])])
        };

        var func = new IrNode.FuncDef("test", [],
                new ZType.ZNamedType("Result", [ZType.Int, new ZType.ZNamedType("ErrorInfo", [])]),
                tryCatch, false)
            { Type = new ZType.ZFuncType([], new ZType.ZNamedType("Result", [ZType.Int, new ZType.ZNamedType("ErrorInfo", [])])) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass",
            importedModules: StdlibModules);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Collection Construction ──────────────────────────────────────

    [Fact]
    public void EmitListNew()
    {
        var listType = new ZType.ZNamedType("List", [ZType.Int]);
        var func = new IrNode.FuncDef("makeList", [], listType,
                new IrNode.ListNew([
                    new IrNode.IntConst(1) { Type = ZType.Int },
                    new IrNode.IntConst(2) { Type = ZType.Int },
                    new IrNode.IntConst(3) { Type = ZType.Int }
                ]) { Type = listType },
                false)
            { Type = new ZType.ZFuncType([], listType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitVectorNew()
    {
        var vecType = new ZType.ZNamedType("Vector", [ZType.Int]);
        var func = new IrNode.FuncDef("makeVec", [], vecType,
                new IrNode.VectorNew([
                    new IrNode.IntConst(10) { Type = ZType.Int },
                    new IrNode.IntConst(20) { Type = ZType.Int }
                ]) { Type = vecType },
                false)
            { Type = new ZType.ZFuncType([], vecType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMapNew()
    {
        var mapType = new ZType.ZNamedType("Map", [ZType.String, ZType.Int]);
        var func = new IrNode.FuncDef("makeMap", [], mapType,
                new IrNode.MapNew([
                    (new IrNode.StringConst("a") { Type = ZType.String }, new IrNode.IntConst(1) { Type = ZType.Int }),
                    (new IrNode.StringConst("b") { Type = ZType.String }, new IrNode.IntConst(2) { Type = ZType.Int })
                ]) { Type = mapType },
                false)
            { Type = new ZType.ZFuncType([], mapType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── CLR Interop ──────────────────────────────────────────────────

    [Fact]
    public void EmitClrNew()
    {
        var sbType = new ZType.ZNamedType("StringBuilder", []);
        var func = new IrNode.FuncDef("makeSb", [], sbType,
                new IrNode.ClrNew("System.Text.StringBuilder", []) { Type = sbType },
                false)
            { Type = new ZType.ZFuncType([], sbType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Exception Handling ───────────────────────────────────────────

    [Fact]
    public void EmitThrow()
    {
        var exnType = new ZType.ZNamedType("Exception", []);
        var func = new IrNode.FuncDef("fail", [], ZType.Int,
                new IrNode.Throw(
                        new IrNode.ClrNew("System.Exception",
                                [new IrNode.StringConst("boom") { Type = ZType.String }])
                            { Type = exnType })
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Pattern Matching ─────────────────────────────────────────────

    [Fact]
    public void EmitMatchWithLiteralPattern()
    {
        var func = new IrNode.FuncDef("describe", [new IrParam("x", ZType.Int)],
                ZType.String,
                new IrNode.Match(
                    new IrNode.Var("x") { Type = ZType.Int },
                    [
                        new IrMatchArm(
                            new IrPattern.Literal(0),
                            new IrNode.StringConst("zero") { Type = ZType.String }),
                        new IrMatchArm(
                            new IrPattern.Literal(1),
                            new IrNode.StringConst("one") { Type = ZType.String }),
                        new IrMatchArm(
                            new IrPattern.Wildcard(),
                            new IrNode.StringConst("other") { Type = ZType.String })
                    ]) { Type = ZType.String },
                false)
            { Type = new ZType.ZFuncType([ZType.Int], ZType.String) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Generics ─────────────────────────────────────────────────────

    [Fact]
    public void EmitGenericRecordDecl()
    {
        var recordDecl = new IrNode.RecordDecl("Pair", ["a", "b"],
        [
            new IrField("first", new ZType.ZNamedType("a", [])),
            new IrField("second", new ZType.ZNamedType("b", []))
        ]);

        var seq = new IrNode.Seq([recordDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitGenericUnionDecl()
    {
        var unionDecl = new IrNode.UnionDecl("Maybe", ["a"],
        [
            new IrUnionCase("Just", [new IrField("value", new ZType.ZNamedType("a", []))]),
            new IrUnionCase("Nothing", [])
        ]);

        var seq = new IrNode.Seq([unionDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Compound Expressions ─────────────────────────────────────────

    [Fact]
    public void EmitNestedLetBindings()
    {
        var body = new IrNode.Let("x",
                new IrNode.IntConst(1) { Type = ZType.Int },
                new IrNode.Let("y",
                        new IrNode.BinOp("+",
                                new IrNode.Var("x") { Type = ZType.Int },
                                new IrNode.IntConst(2) { Type = ZType.Int })
                            { Type = ZType.Int },
                        new IrNode.Let("z",
                                new IrNode.BinOp("*",
                                        new IrNode.Var("x") { Type = ZType.Int },
                                        new IrNode.Var("y") { Type = ZType.Int })
                                    { Type = ZType.Int },
                                new IrNode.Var("z") { Type = ZType.Int })
                            { Type = ZType.Int })
                    { Type = ZType.Int })
            { Type = ZType.Int };

        var func = new IrNode.FuncDef("test", [], ZType.Int, body, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitSeqWithSideEffects()
    {
        var func = new IrNode.FuncDef("test", [], ZType.Int,
                new IrNode.Seq([
                    new IrNode.IntConst(1) { Type = ZType.Int },
                    new IrNode.IntConst(2) { Type = ZType.Int },
                    new IrNode.IntConst(3) { Type = ZType.Int }
                ]) { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Lambda / Closure ─────────────────────────────────────────────

    [Fact]
    public void EmitLambda_ZeroCapture()
    {
        var outer = new IrNode.FuncDef("make-inc",
                [], new ZType.ZFuncType([ZType.Int], ZType.Int),
                new IrNode.FuncDef("inc", [new IrParam("x", ZType.Int)], ZType.Int,
                        new IrNode.BinOp("+",
                            new IrNode.Var("x") { Type = ZType.Int },
                            new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
                        false)
                    { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) },
                false)
            { Type = new ZType.ZFuncType([], new ZType.ZFuncType([ZType.Int], ZType.Int)) };

        var seq = new IrNode.Seq([outer]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestLambdaAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitLambda_WithCapture_FromLetBinding()
    {
        var outer = new IrNode.FuncDef("make-adder",
                [new IrParam("y", ZType.Int)], new ZType.ZFuncType([ZType.Int], ZType.Int),
                new IrNode.Let("captured",
                        new IrNode.Var("y") { Type = ZType.Int },
                        new IrNode.FuncDef("adder", [new IrParam("x", ZType.Int)], ZType.Int,
                                new IrNode.BinOp("+",
                                    new IrNode.Var("x") { Type = ZType.Int },
                                    new IrNode.Var("captured") { Type = ZType.Int }) { Type = ZType.Int },
                                false)
                            { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) })
                    { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) },
                false)
            { Type = new ZType.ZFuncType([ZType.Int], new ZType.ZFuncType([ZType.Int], ZType.Int)) };

        var seq = new IrNode.Seq([outer]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestClosureAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Async ────────────────────────────────────────────────────────

    private static readonly ZType TaskInt = new ZType.ZNamedType("Task", [ZType.Int]);
    private static readonly ZType TaskUnit = new ZType.ZNamedType("Task", []);

    [Fact]
    public void AsyncSingleAwait_EmitsStateMachine()
    {
        var computeAsync = new IrNode.FuncDef("compute-async",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.BinOp("+",
                    new IrNode.Var("x") { Type = ZType.Int },
                    new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskInt) };

        var fetchAndAdd = new IrNode.FuncDef("fetch-and-add",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.Let("result",
                        new IrNode.Await(
                                new IrNode.Call(
                                    new IrNode.Var("compute-async")
                                        { Type = new ZType.ZFuncType([ZType.Int], TaskInt) },
                                    [new IrNode.Var("x") { Type = ZType.Int }]) { Type = TaskInt })
                            { Type = ZType.Int },
                        new IrNode.BinOp("+",
                            new IrNode.Var("result") { Type = ZType.Int },
                            new IrNode.IntConst(10) { Type = ZType.Int }) { Type = ZType.Int })
                    { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskInt) };

        var seq = new IrNode.Seq([computeAsync, fetchAndAdd]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAsyncAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void AsyncWithoutAwait_UsesSimplePath()
    {
        var func = new IrNode.FuncDef("simple-async",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.BinOp("+",
                    new IrNode.Var("x") { Type = ZType.Int },
                    new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskInt) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAsyncAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl()
    {
        var classDecl = new IrNode.ClassDecl("Counter", [], [],
            [new IrField("count", ZType.Int)],
            [
                new IrObjectMethod("getCount", [], ZType.Int,
                    new IrNode.Var("count") { Type = ZType.Int })
            ]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void HasEntryPointTrueForMainFunction()
    {
        var listStringType = new ZType.ZNamedType("List", [ZType.String]);
        var func = new IrNode.FuncDef("main",
                [new IrParam("args", listStringType)],
                ZType.Int,
                new IrNode.IntConst(0) { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([listStringType], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new AsmResolverEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.True(emitter.HasEntryPoint);
    }
}
