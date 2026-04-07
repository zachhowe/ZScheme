using System.Reflection;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

public class IlEmitterTests
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

    // ─── Async ────────────────────────────────────────────────────────

    private static readonly ZType TaskInt = new ZType.ZNamedType("Task", [ZType.Int]);
    private static readonly ZType TaskUnit = new ZType.ZNamedType("Task", []);

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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass",
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnionCaseNew_GenericSingleTypeArg()
    {
        var unionDecl = new IrNode.UnionDecl("Option", ["a"],
        [
            new IrUnionCase("Some", [new IrField("value", new ZType.ZNamedType("a", []))]),
            new IrUnionCase("None", [])
        ]);

        var optionIntType = new ZType.ZNamedType("Option", [ZType.Int]);
        var func = new IrNode.FuncDef("test", [], optionIntType,
                new IrNode.UnionCaseNew("Option", "Some",
                    [new IrNode.IntConst(42) { Type = ZType.Int }]) { Type = optionIntType },
                false)
            { Type = new ZType.ZFuncType([], optionIntType) };

        var seq = new IrNode.Seq([unionDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnionCaseNew_GenericMultipleTypeArgs()
    {
        var unionDecl = new IrNode.UnionDecl("Result", ["a", "e"],
        [
            new IrUnionCase("Ok", [new IrField("value", new ZType.ZNamedType("a", []))]),
            new IrUnionCase("Err", [new IrField("error", new ZType.ZNamedType("e", []))])
        ]);

        var resultType = new ZType.ZNamedType("Result", [ZType.Int, ZType.String]);
        var func = new IrNode.FuncDef("test", [], resultType,
                new IrNode.UnionCaseNew("Result", "Ok",
                    [new IrNode.IntConst(1) { Type = ZType.Int }]) { Type = resultType },
                false)
            { Type = new ZType.ZFuncType([], resultType) };

        var seq = new IrNode.Seq([unionDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnionCaseNew_GenericNullaryCase()
    {
        var unionDecl = new IrNode.UnionDecl("Option", ["a"],
        [
            new IrUnionCase("Some", [new IrField("value", new ZType.ZNamedType("a", []))]),
            new IrUnionCase("None", [])
        ]);

        var optionIntType = new ZType.ZNamedType("Option", [ZType.Int]);
        var func = new IrNode.FuncDef("test", [], optionIntType,
                new IrNode.UnionCaseNew("Option", "None", []) { Type = optionIntType },
                false)
            { Type = new ZType.ZFuncType([], optionIntType) };

        var seq = new IrNode.Seq([unionDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnionCaseNew_GenericNestedTypeArg()
    {
        var unionDecl = new IrNode.UnionDecl("Option", ["a"],
        [
            new IrUnionCase("Some", [new IrField("value", new ZType.ZNamedType("a", []))]),
            new IrUnionCase("None", [])
        ]);

        var nestedType = new ZType.ZNamedType("Option", [new ZType.ZNamedType("Option", [ZType.Int])]);
        var func = new IrNode.FuncDef("test", [], nestedType,
                new IrNode.UnionCaseNew("Option", "Some",
                [
                    new IrNode.UnionCaseNew("Option", "Some",
                            [new IrNode.IntConst(7) { Type = ZType.Int }])
                        { Type = new ZType.ZNamedType("Option", [ZType.Int]) }
                ]) { Type = nestedType },
                false)
            { Type = new ZType.ZFuncType([], nestedType) };

        var seq = new IrNode.Seq([unionDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }


    // ─── Collection Construction ──────────────────────────────────────


    // ─── CLR Interop ──────────────────────────────────────────────────

    [Fact]
    public void EmitClrNew()
    {
        var sbType = new ZType.ZNamedType("StringBuilder", []);
        var func = new IrNode.FuncDef("makeSb", [], sbType,
                new IrNode.ClrNew("System.Text.StringBuilder", [], []) { Type = sbType },
                false)
            { Type = new ZType.ZFuncType([], sbType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
                        new IrNode.ClrNew("System.Exception", [],
                                [new IrNode.StringConst("boom") { Type = ZType.String }])
                            { Type = exnType })
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestLambdaAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestClosureAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

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
        var emitter = new IlEmitter("TestAsyncAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAsyncAssembly", diag, "TestClass");
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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl_OpenClass()
    {
        var baseDecl = new IrNode.ClassDecl("Animal", [], [],
            [new IrField("name", ZType.String)],
            [
                new IrObjectMethod("Speak", [], ZType.String,
                    new IrNode.Var("name") { Type = ZType.String })
            ],
            true);

        var seq = new IrNode.Seq([baseDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl_Inheritance()
    {
        var baseDecl = new IrNode.ClassDecl("Animal", [], [],
            [new IrField("name", ZType.String)],
            [
                new IrObjectMethod("Speak", [], ZType.String,
                    new IrNode.Var("name") { Type = ZType.String })
            ],
            true);

        var subDecl = new IrNode.ClassDecl("Dog", [], [],
            [new IrField("breed", ZType.String)],
            [
                new IrObjectMethod("Speak", [], ZType.String,
                    new IrNode.Var("breed") { Type = ZType.String })
            ],
            BaseClassName: "Animal");

        var seq = new IrNode.Seq([baseDecl, subDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl_ExplicitConstructor()
    {
        var ctor = new IrConstructor(
            [new IrParam("n", ZType.String)],
            null,
            [("name", new IrNode.Var("n") { Type = ZType.String })],
            []);

        var classDecl = new IrNode.ClassDecl("Widget", [], [],
            [new IrField("name", ZType.String)],
            [],
            Constructor: ctor);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl_ExplicitConstructorWithSuper()
    {
        var baseDecl = new IrNode.ClassDecl("Animal", [], [],
            [new IrField("name", ZType.String)],
            [],
            true);

        var ctor = new IrConstructor(
            [new IrParam("nick", ZType.String)],
            [new IrNode.Var("nick") { Type = ZType.String }],
            [("breed", new IrNode.StringConst("mixed") { Type = ZType.String })],
            []);

        var subDecl = new IrNode.ClassDecl("Dog", [], [],
            [new IrField("breed", ZType.String)],
            [],
            BaseClassName: "Animal",
            Constructor: ctor);

        var seq = new IrNode.Seq([baseDecl, subDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Object Expressions ─────────────────────────────────────────────

    [Fact]
    public void EmitObjectExpr_WithZSchemeInterface()
    {
        var ifaceDecl = new IrNode.InterfaceDecl("IGreeter", [], [],
            [new IrInterfaceMethodSignature("Greet", [new IrParam("name", ZType.String)], ZType.String)]);

        var objectExpr = new IrNode.ObjectExpr(
            ["IGreeter"],
            [
                new IrObjectMethod("Greet", [new IrParam("name", ZType.String)], ZType.String,
                    new IrNode.Var("name") { Type = ZType.String })
            ]) { Type = new ZType.ZNamedType("IGreeter", []) };

        var func = new IrNode.FuncDef("makeGreeter", [], new ZType.ZNamedType("IGreeter", []),
                objectExpr, false)
            { Type = new ZType.ZFuncType([], new ZType.ZNamedType("IGreeter", [])) };

        var seq = new IrNode.Seq([ifaceDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitObjectExpr_NoCapturedVariables()
    {
        var ifaceDecl = new IrNode.InterfaceDecl("ICounter", [], [],
            [new IrInterfaceMethodSignature("GetValue", [], ZType.Int)]);

        var objectExpr = new IrNode.ObjectExpr(
            ["ICounter"],
            [
                new IrObjectMethod("GetValue", [], ZType.Int,
                    new IrNode.IntConst(42) { Type = ZType.Int })
            ]) { Type = new ZType.ZNamedType("ICounter", []) };

        var func = new IrNode.FuncDef("makeCounter", [], new ZType.ZNamedType("ICounter", []),
                objectExpr, false)
            { Type = new ZType.ZFuncType([], new ZType.ZNamedType("ICounter", [])) };

        var seq = new IrNode.Seq([ifaceDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitObjectExpr_WithCapturedVariables()
    {
        var ifaceDecl = new IrNode.InterfaceDecl("IGreeter", [], [],
            [new IrInterfaceMethodSignature("Greet", [], ZType.String)]);

        var objectExpr = new IrNode.ObjectExpr(
            ["IGreeter"],
            [
                new IrObjectMethod("Greet", [], ZType.String,
                    new IrNode.Var("greeting") { Type = ZType.String })
            ]) { Type = new ZType.ZNamedType("IGreeter", []) };

        var letExpr = new IrNode.Let("greeting",
                new IrNode.StringConst("Hello") { Type = ZType.String },
                objectExpr)
            { Type = new ZType.ZNamedType("IGreeter", []) };

        var func = new IrNode.FuncDef("makeGreeter", [], new ZType.ZNamedType("IGreeter", []),
                letExpr, false)
            { Type = new ZType.ZFuncType([], new ZType.ZNamedType("IGreeter", [])) };

        var seq = new IrNode.Seq([ifaceDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitObjectExpr_MultipleInterfaces()
    {
        var iface1 = new IrNode.InterfaceDecl("IFoo", [], [],
            [new IrInterfaceMethodSignature("Foo", [], ZType.Int)]);
        var iface2 = new IrNode.InterfaceDecl("IBar", [], [],
            [new IrInterfaceMethodSignature("Bar", [], ZType.String)]);

        var objectExpr = new IrNode.ObjectExpr(
            ["IFoo", "IBar"],
            [
                new IrObjectMethod("Foo", [], ZType.Int,
                    new IrNode.IntConst(1) { Type = ZType.Int }),
                new IrObjectMethod("Bar", [], ZType.String,
                    new IrNode.StringConst("bar") { Type = ZType.String })
            ]) { Type = new ZType.ZNamedType("IFoo", []) };

        var func = new IrNode.FuncDef("make", [], new ZType.ZNamedType("IFoo", []),
                objectExpr, false)
            { Type = new ZType.ZFuncType([], new ZType.ZNamedType("IFoo", [])) };

        var seq = new IrNode.Seq([iface1, iface2, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitObjectExpr_WithBaseClass()
    {
        var baseDecl = new IrNode.ClassDecl("Animal", [], [],
            [new IrField("name", ZType.String)],
            [
                new IrObjectMethod("Speak", [], ZType.String,
                    new IrNode.Var("name") { Type = ZType.String })
            ],
            true);

        var objectExpr = new IrNode.ObjectExpr(
            [],
            [
                new IrObjectMethod("Speak", [], ZType.String,
                    new IrNode.StringConst("meow") { Type = ZType.String })
            ],
            "Animal") { Type = new ZType.ZNamedType("Animal", []) };

        var func = new IrNode.FuncDef("makeCat", [], new ZType.ZNamedType("Animal", []),
                objectExpr, false)
            { Type = new ZType.ZFuncType([], new ZType.ZNamedType("Animal", [])) };

        var seq = new IrNode.Seq([baseDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitObjectExpr_WithBaseClassAndConstructorSuper()
    {
        var baseDecl = new IrNode.ClassDecl("Animal", [], [],
            [new IrField("name", ZType.String)],
            [],
            true);

        var irCtor = new IrConstructor(
            [],
            [new IrNode.StringConst("Cat") { Type = ZType.String }],
            [],
            []);

        var objectExpr = new IrNode.ObjectExpr(
            [],
            [],
            "Animal",
            irCtor) { Type = new ZType.ZNamedType("Animal", []) };

        var func = new IrNode.FuncDef("makeCat", [], new ZType.ZNamedType("Animal", []),
                objectExpr, false)
            { Type = new ZType.ZFuncType([], new ZType.ZNamedType("Animal", [])) };

        var seq = new IrNode.Seq([baseDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitInterfaceDecl_WithBaseInterface()
    {
        var baseIface = new IrNode.InterfaceDecl("IBase", [], [],
            [new IrInterfaceMethodSignature("BaseMethod", [], ZType.Int)]);

        var childIface = new IrNode.InterfaceDecl("IChild", [], ["IBase"],
            [new IrInterfaceMethodSignature("ChildMethod", [], ZType.String)]);

        var seq = new IrNode.Seq([baseIface, childIface]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl_WithInterface()
    {
        var ifaceDecl = new IrNode.InterfaceDecl("IGreeter", [], [],
            [new IrInterfaceMethodSignature("Greet", [], ZType.String)]);

        var classDecl = new IrNode.ClassDecl("HelloGreeter", [], ["IGreeter"],
            [new IrField("name", ZType.String)],
            [
                new IrObjectMethod("Greet", [], ZType.String,
                    new IrNode.Var("name") { Type = ZType.String })
            ]);

        var seq = new IrNode.Seq([ifaceDecl, classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Entry Point ──────────────────────────────────────────────────

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
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.True(emitter.HasEntryPoint);
    }

    // ─── Error Propagation ───────────────────────────────────────────

    [Fact]
    public void EmitPropagate()
    {
        var errorInfoType = new ZType.ZNamedType("ErrorInfo", []);
        var resultIntType = new ZType.ZNamedType("Result", [ZType.Int, errorInfoType]);

        // Helper function that returns Result<Int, ErrorInfo>
        var helper = new IrNode.FuncDef("helper",
                [new IrParam("x", ZType.Int)],
                resultIntType,
                new IrNode.UnionCaseNew("Result", "Ok",
                    [new IrNode.BinOp("/",
                            new IrNode.IntConst(10) { Type = ZType.Int },
                            new IrNode.Var("x") { Type = ZType.Int })
                        { Type = ZType.Int }])
                    { Type = resultIntType },
                false)
            { Type = new ZType.ZFuncType([ZType.Int], resultIntType) };

        // Caller uses ? to propagate errors
        var helperFuncType = new ZType.ZFuncType([ZType.Int], resultIntType);
        var caller = new IrNode.FuncDef("caller",
                [new IrParam("x", ZType.Int)],
                resultIntType,
                new IrNode.Let("v",
                        new IrNode.Propagate(
                                new IrNode.Call(
                                        new IrNode.Var("helper") { Type = helperFuncType },
                                        [new IrNode.Var("x") { Type = ZType.Int }])
                                    { Type = resultIntType },
                                resultIntType)
                            { Type = ZType.Int },
                        new IrNode.BinOp("+",
                                new IrNode.Var("v") { Type = ZType.Int },
                                new IrNode.IntConst(1) { Type = ZType.Int })
                            { Type = ZType.Int })
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([ZType.Int], resultIntType) };

        var seq = new IrNode.Seq([helper, caller]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass", importedModules: StdlibModules);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Method Calls on Collections ──────────────────────────────────

    [Fact]
    public void EmitMethodCall_Property()
    {
        var arrType = new ZType.ZNamedType("Mutable-Array", [ZType.Int]);
        var func = new IrNode.FuncDef("arrLength", [], ZType.Int,
                new IrNode.MethodCall(
                        new IrNode.MutableArrayNew(ZType.Int, [
                            new IrNode.IntConst(1) { Type = ZType.Int },
                            new IrNode.IntConst(2) { Type = ZType.Int }
                        ]) { Type = arrType },
                        "Length", [], true, false)
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMethodCall_Indexer()
    {
        var arrType = new ZType.ZNamedType("Mutable-Array", [ZType.Int]);
        var func = new IrNode.FuncDef("getFirst", [], ZType.Int,
                new IrNode.MethodCall(
                        new IrNode.MutableArrayNew(ZType.Int, [
                            new IrNode.IntConst(42) { Type = ZType.Int }
                        ]) { Type = arrType },
                        "Get",
                        [new IrNode.IntConst(0) { Type = ZType.Int }],
                        false, true)
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMethodCall_PropertySet()
    {
        var listType = new ZType.ZNamedType("Mutable-List", [ZType.Int]);
        var param = new IrParam("lst", listType);
        var func = new IrNode.FuncDef("setCapacity", [param], ZType.Unit,
                new IrNode.MethodCall(
                        new IrNode.Var("lst") { Type = listType },
                        "Capacity",
                        [new IrNode.IntConst(10) { Type = ZType.Int }],
                        false, false, true)
                    { Type = ZType.Unit },
                false)
            { Type = new ZType.ZFuncType([listType], ZType.Unit) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMethodCall_Indexer_NonArray()
    {
        var listType = new ZType.ZNamedType("Mutable-List", [ZType.Int]);
        var param = new IrParam("lst", listType);
        var func = new IrNode.FuncDef("getFirst", [param], ZType.Int,
                new IrNode.MethodCall(
                        new IrNode.Var("lst") { Type = listType },
                        "Get",
                        [new IrNode.IntConst(0) { Type = ZType.Int }],
                        false, true)
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([listType], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMethodCall_Indexer_ErrorWhenNotFound()
    {
        var param = new IrParam("s", ZType.String);
        var func = new IrNode.FuncDef("getChar", [param], ZType.Int,
                new IrNode.MethodCall(
                        new IrNode.Var("s") { Type = ZType.String },
                        "Get",
                        [new IrNode.IntConst(0) { Type = ZType.Int }],
                        false, true)
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([ZType.String], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        emitter.Emit(seq);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Indexer not found"));
    }

    [Fact]
    public void EmitMethodCall_IndexerSet()
    {
        var arrType = new ZType.ZNamedType("Mutable-Array", [ZType.Int]);
        var func = new IrNode.FuncDef("setFirst", [], ZType.Unit,
                new IrNode.MethodCall(
                        new IrNode.MutableArrayNew(ZType.Int, [
                            new IrNode.IntConst(0) { Type = ZType.Int }
                        ]) { Type = arrType },
                        "Set",
                        [
                            new IrNode.IntConst(0) { Type = ZType.Int },
                            new IrNode.IntConst(99) { Type = ZType.Int }
                        ],
                        false, false, IsIndexerSet: true)
                    { Type = ZType.Unit },
                false)
            { Type = new ZType.ZFuncType([], ZType.Unit) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMethodCall_IndexerSet_NonArray()
    {
        var listType = new ZType.ZNamedType("Mutable-List", [ZType.Int]);
        var param = new IrParam("lst", listType);
        var func = new IrNode.FuncDef("setFirst", [param], ZType.Unit,
                new IrNode.MethodCall(
                        new IrNode.Var("lst") { Type = listType },
                        "Set",
                        [
                            new IrNode.IntConst(0) { Type = ZType.Int },
                            new IrNode.IntConst(99) { Type = ZType.Int }
                        ],
                        false, false, IsIndexerSet: true)
                    { Type = ZType.Unit },
                false)
            { Type = new ZType.ZFuncType([listType], ZType.Unit) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Attributes ───────────────────────────────────────────────────

    [Fact]
    public void EmitFuncDefWithAttributes()
    {
        var func = new IrNode.FuncDef("oldFunc", [], ZType.Int,
                new IrNode.IntConst(42) { Type = ZType.Int },
                false,
                Attributes: [new IrAttribute("System.ObsoleteAttribute", [], [])])
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Lambda with Outer Parameter Capture ──────────────────────────

    [Fact]
    public void EmitLambda_WithCapture_FromOuterParam()
    {
        // (define (make-multiplier [factor : Int]) : (-> Int Int) (fn [x] (* x factor)))
        // Lambda captures "factor" directly from outer param — outerParams resolution path
        var outer = new IrNode.FuncDef("make-multiplier",
                [new IrParam("factor", ZType.Int)], new ZType.ZFuncType([ZType.Int], ZType.Int),
                new IrNode.FuncDef("mul", [new IrParam("x", ZType.Int)], ZType.Int,
                        new IrNode.BinOp("*",
                            new IrNode.Var("x") { Type = ZType.Int },
                            new IrNode.Var("factor") { Type = ZType.Int }) { Type = ZType.Int },
                        false)
                    { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) },
                false)
            { Type = new ZType.ZFuncType([ZType.Int], new ZType.ZFuncType([ZType.Int], ZType.Int)) };

        var seq = new IrNode.Seq([outer]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestCaptureParamAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Advanced Async ──────────────────────────────────────────────

    [Fact]
    public void AsyncMultipleAwait_EmitsStateMachine()
    {
        var computeAsync = new IrNode.FuncDef("compute-async",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.BinOp("+",
                    new IrNode.Var("x") { Type = ZType.Int },
                    new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskInt) };

        // double-compute with two chained awaits
        var doubleCompute = new IrNode.FuncDef("double-compute",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.Let("a",
                        new IrNode.Await(
                                new IrNode.Call(
                                    new IrNode.Var("compute-async")
                                        { Type = new ZType.ZFuncType([ZType.Int], TaskInt) },
                                    [new IrNode.Var("x") { Type = ZType.Int }]) { Type = TaskInt })
                            { Type = ZType.Int },
                        new IrNode.Let("b",
                                new IrNode.Await(
                                        new IrNode.Call(
                                            new IrNode.Var("compute-async")
                                                { Type = new ZType.ZFuncType([ZType.Int], TaskInt) },
                                            [new IrNode.Var("a") { Type = ZType.Int }]) { Type = TaskInt })
                                    { Type = ZType.Int },
                                new IrNode.BinOp("+",
                                    new IrNode.Var("a") { Type = ZType.Int },
                                    new IrNode.Var("b") { Type = ZType.Int }) { Type = ZType.Int })
                            { Type = ZType.Int })
                    { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskInt) };

        var seq = new IrNode.Seq([computeAsync, doubleCompute]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAsyncAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void AsyncVoidReturn_EmitsStateMachine()
    {
        var computeAsync = new IrNode.FuncDef("compute-async",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.BinOp("+",
                    new IrNode.Var("x") { Type = ZType.Int },
                    new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskInt) };

        // (define-async (do-work) : Task (await (compute-async 42)))
        var doWork = new IrNode.FuncDef("do-work",
                [], ZType.Unit,
                new IrNode.Await(
                        new IrNode.Call(
                            new IrNode.Var("compute-async") { Type = new ZType.ZFuncType([ZType.Int], TaskInt) },
                            [new IrNode.IntConst(42) { Type = ZType.Int }]) { Type = TaskInt })
                    { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskUnit) };

        var seq = new IrNode.Seq([computeAsync, doWork]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAsyncAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitSyncAwait_TaskOfInt()
    {
        // An async helper: (define-async (compute-async [x : Int]) : (Task Int) (+ x 1))
        var computeAsync = new IrNode.FuncDef("compute-async",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.BinOp("+",
                    new IrNode.Var("x") { Type = ZType.Int },
                    new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskInt) };

        // A NON-async function that synchronously awaits: hits EmitAwait (not EmitMoveNextAwait)
        var syncCaller = new IrNode.FuncDef("sync-caller",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.Await(
                        new IrNode.Call(
                            new IrNode.Var("compute-async")
                                { Type = new ZType.ZFuncType([ZType.Int], TaskInt) },
                            [new IrNode.Var("x") { Type = ZType.Int }]) { Type = TaskInt })
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) };

        var seq = new IrNode.Seq([computeAsync, syncCaller]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestSyncAwaitAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitSyncAwait_TaskUnit()
    {
        // An async helper returning Task (void): (define-async (do-work-async) : (Task) (unit))
        var doWorkAsync = new IrNode.FuncDef("do-work-async",
                [], ZType.Unit,
                new IrNode.UnitConst { Type = ZType.Unit },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskUnit) };

        // A NON-async function that synchronously awaits Task (void result)
        var syncCaller = new IrNode.FuncDef("sync-caller",
                [], ZType.Unit,
                new IrNode.Await(
                        new IrNode.Call(
                            new IrNode.Var("do-work-async")
                                { Type = new ZType.ZFuncType([], TaskUnit) },
                            []) { Type = TaskUnit })
                    { Type = ZType.Unit },
                false)
            { Type = new ZType.ZFuncType([], ZType.Unit) };

        var seq = new IrNode.Seq([doWorkAsync, syncCaller]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestSyncAwaitUnitAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Interface Declaration ────────────────────────────────────────

    [Fact]
    public void EmitInterfaceDecl()
    {
        var ifaceDecl = new IrNode.InterfaceDecl("IGreeter", [], [],
            [new IrInterfaceMethodSignature("greet", [new IrParam("name", ZType.String)], ZType.String)]);

        var seq = new IrNode.Seq([ifaceDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Nested Type Declaration Tests ──────────────────────────────────

    [Fact]
    public void EmitRecordInModule_BecomesNestedType()
    {
        var recordDecl = new IrNode.RecordDecl("Point", [],
        [
            new IrField("x", ZType.Int),
            new IrField("y", ZType.Int)
        ]);

        var pointType = new ZType.ZNamedType("Point", []);
        var func = new IrNode.FuncDef("origin", [], ZType.Int,
                new IrNode.FieldGet(
                    new IrNode.RecordNew("Point",
                    [
                        ("x", new IrNode.IntConst(0) { Type = ZType.Int }),
                        ("y", new IrNode.IntConst(0) { Type = ZType.Int })
                    ]) { Type = pointType },
                    "x") { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([recordDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestNestedAssembly", diag, "TestModule", isModule: true);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // Load the emitted assembly and verify Point is a nested type of TestModule
        var asm = Assembly.Load(bytes);
        var moduleType = asm.GetType("TestNestedAssembly.TestModule");
        Assert.NotNull(moduleType);
        var nestedPoint = moduleType!.GetNestedType("Point");
        Assert.NotNull(nestedPoint);
    }

    [Fact]
    public void EmitRecordWithoutModule_StaysTopLevel()
    {
        var recordDecl = new IrNode.RecordDecl("Point", [],
        [
            new IrField("x", ZType.Int),
            new IrField("y", ZType.Int)
        ]);

        var seq = new IrNode.Seq([recordDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestTopLevelAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // Load the emitted assembly and verify Point is a top-level type
        var asm = Assembly.Load(bytes);
        var pointType = asm.GetType("TestTopLevelAssembly.Point");
        Assert.NotNull(pointType);
    }

    // --- WithHandlers ---

    [Fact]
    public void EmitWithHandlers()
    {
        var withHandlers = new IrNode.WithHandlers(
                new IrNode.IntConst(42) { Type = ZType.Int },
                [
                    new IrHandlerClause("System.Exception", "e",
                        new IrNode.IntConst(0) { Type = ZType.Int })
                ])
            { Type = ZType.Int };

        var func = new IrNode.FuncDef("test", [], ZType.Int, withHandlers, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── SuperMethodCall ────────────────────────────────────────────────

    [Fact]
    public void EmitSuperMethodCall_CallsBaseMethod()
    {
        var baseDecl = new IrNode.ClassDecl("Animal", [], [],
            [new IrField("name", ZType.String)],
            [
                new IrObjectMethod("Speak", [], ZType.String,
                    new IrNode.Var("name") { Type = ZType.String })
            ],
            true);

        var subDecl = new IrNode.ClassDecl("Dog", [], [],
            [new IrField("breed", ZType.String)],
            [
                new IrObjectMethod("Speak", [], ZType.String,
                    new IrNode.SuperMethodCall("Speak", []) { Type = ZType.String })
            ],
            BaseClassName: "Animal");

        var seq = new IrNode.Seq([baseDecl, subDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitSuperMethodCall_WithArguments()
    {
        var baseDecl = new IrNode.ClassDecl("Base", [], [],
            [],
            [
                new IrObjectMethod("Add",
                    [new IrParam("a", ZType.Int), new IrParam("b", ZType.Int)],
                    ZType.Int,
                    new IrNode.BinOp("+",
                            new IrNode.Var("a") { Type = ZType.Int },
                            new IrNode.Var("b") { Type = ZType.Int })
                        { Type = ZType.Int })
            ],
            true);

        var subDecl = new IrNode.ClassDecl("Sub", [], [],
            [],
            [
                new IrObjectMethod("Add",
                    [new IrParam("a", ZType.Int), new IrParam("b", ZType.Int)],
                    ZType.Int,
                    new IrNode.SuperMethodCall("Add",
                    [
                        new IrNode.Var("a") { Type = ZType.Int },
                        new IrNode.Var("b") { Type = ZType.Int }
                    ]) { Type = ZType.Int })
            ],
            BaseClassName: "Base");

        var seq = new IrNode.Seq([baseDecl, subDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitSuperMethodCall_ErrorNoBaseClass()
    {
        var classDecl = new IrNode.ClassDecl("Standalone", [], [],
            [],
            [
                new IrObjectMethod("DoStuff", [], ZType.Int,
                    new IrNode.SuperMethodCall("DoStuff", []) { Type = ZType.Int })
            ]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        emitter.Emit(seq);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics,
            d => d.Message.Contains("super/ can only be used in a class with a base class"));
    }

    [Fact]
    public void EmitSuperMethodCall_ErrorMethodNotFound()
    {
        var baseDecl = new IrNode.ClassDecl("Animal", [], [],
            [new IrField("name", ZType.String)],
            [
                new IrObjectMethod("Speak", [], ZType.String,
                    new IrNode.Var("name") { Type = ZType.String })
            ],
            true);

        var subDecl = new IrNode.ClassDecl("Dog", [], [],
            [],
            [
                new IrObjectMethod("Bark", [], ZType.String,
                    new IrNode.SuperMethodCall("NonExistent", []) { Type = ZType.String })
            ],
            BaseClassName: "Animal");

        var seq = new IrNode.Seq([baseDecl, subDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        emitter.Emit(seq);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics,
            d => d.Message.Contains("Base class has no method 'NonExistent'"));
    }

    // ─── DelegateInvoke ─────────────────────────────────────────────────

    [Fact]
    public void EmitDelegateInvoke_ViaParameter()
    {
        var funcType = new ZType.ZFuncType([ZType.Int], ZType.Int);

        var func = new IrNode.FuncDef("apply",
                [new IrParam("f", funcType), new IrParam("x", ZType.Int)],
                ZType.Int,
                new IrNode.Call(
                        new IrNode.Var("f") { Type = funcType },
                        [new IrNode.Var("x") { Type = ZType.Int }])
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([funcType, ZType.Int], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitDelegateInvoke_ViaLetBinding()
    {
        var funcType = new ZType.ZFuncType([ZType.Int], ZType.Int);

        // apply(f) = let g = f in g(5)
        // The let-binding stores a delegate param into a local, then calls via local
        var func = new IrNode.FuncDef("apply",
                [new IrParam("f", funcType)],
                ZType.Int,
                new IrNode.Let("g",
                        new IrNode.Var("f") { Type = funcType },
                        new IrNode.Call(
                                new IrNode.Var("g") { Type = funcType },
                                [new IrNode.IntConst(5) { Type = ZType.Int }])
                            { Type = ZType.Int })
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([funcType], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitDelegateInvoke_NonVarExpression()
    {
        var innerFuncType = new ZType.ZFuncType([ZType.Int], ZType.Int);

        // A function that takes a Fn(Int)->Fn(Int)->Int parameter and calls its result
        // apply(f, x) = (f(x))(x)  — the inner call returns a delegate, outer call invokes it
        var func = new IrNode.FuncDef("apply",
                [
                    new IrParam("f", new ZType.ZFuncType([ZType.Int], innerFuncType)),
                    new IrParam("x", ZType.Int)
                ],
                ZType.Int,
                new IrNode.Call(
                        new IrNode.Call(
                                new IrNode.Var("f") { Type = new ZType.ZFuncType([ZType.Int], innerFuncType) },
                                [new IrNode.Var("x") { Type = ZType.Int }])
                            { Type = innerFuncType },
                        [new IrNode.Var("x") { Type = ZType.Int }])
                    { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([new ZType.ZFuncType([ZType.Int], innerFuncType), ZType.Int], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ===== Nullable wrapping tests =====

    [Fact]
    public void EmitClassDecl_NullableFieldSetInConstructor()
    {
        // Class with Float? field set to a non-nullable Float literal in constructor
        var ctor = new IrConstructor(
            [],
            null,
            [("duration", new IrNode.FloatConst(3.0f) { Type = ZType.Float })],
            []);

        var classDecl = new IrNode.ClassDecl("Timer", [], [],
            [new IrField("duration", new ZType.ZNullableType(ZType.Float), IsMutable: true)],
            [],
            Constructor: ctor);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl_NullableFieldSetToNullInConstructor()
    {
        // Class with Float? field set to null in constructor
        var ctor = new IrConstructor(
            [],
            null,
            [("duration", new IrNode.NullConst { Type = new ZType.ZTypeVar(99) })],
            []);

        var classDecl = new IrNode.ClassDecl("Timer", [], [],
            [new IrField("duration", new ZType.ZNullableType(ZType.Float), IsMutable: true)],
            [],
            Constructor: ctor);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl_SetFieldNullableWrapping()
    {
        // Class with a method that sets a nullable field to a non-nullable value via SetField
        var setField = new IrNode.SetField("count", new IrNode.IntConst(42) { Type = ZType.Int })
            { Type = ZType.Unit };

        var method = new IrObjectMethod("SetCount",
            [new IrParam("v", ZType.Int)],
            ZType.Unit,
            setField);

        var classDecl = new IrNode.ClassDecl("Counter", [], [],
            [new IrField("count", new ZType.ZNullableType(ZType.Int), IsMutable: true)],
            [method]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ===== Static field / enum fallback tests =====

    [Fact]
    public void EmitClrCall_FallsBackToStaticField_EnumValue()
    {
        // Calling EquipmentSlot.Feet should resolve to the enum static field
        // We use System.DayOfWeek.Friday as a test since it's always available
        var clrCall = new IrNode.ClrCall(
                "System.DayOfWeek",
                "Friday",
                [])
            { Type = new ZType.ZNamedType("System.DayOfWeek", []) };

        var func = new IrNode.FuncDef(
                "GetFriday",
                [],
                new ZType.ZNamedType("System.DayOfWeek", []),
                clrCall,
                false)
            { Type = new ZType.ZFuncType([], new ZType.ZNamedType("System.DayOfWeek", [])) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClrCall_FallsBackToStaticField_StaticReadonly()
    {
        // Accessing string.Empty (static readonly field)
        var clrCall = new IrNode.ClrCall(
                "System.String",
                "Empty",
                [])
            { Type = ZType.String };

        var func = new IrNode.FuncDef(
                "GetEmpty",
                [],
                ZType.String,
                clrCall,
                false)
            { Type = new ZType.ZFuncType([], ZType.String) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Mixed arm types (stack reconciliation) ──────────────────────

    [Fact]
    public void EmitIfWithMixedArmTypes_UnitThenBranch_ValueElseBranch()
    {
        // if (cond) (set! field value) else null
        // Then branch is Unit (SetField), Else branch is Object (NullConst)
        // Overall type is Unit — the non-Unit branch value should be popped
        var setField = new IrNode.SetField("count", new IrNode.IntConst(1) { Type = ZType.Int })
            { Type = ZType.Unit };
        var nullConst = new IrNode.NullConst { Type = new ZType.ZNamedType("System.Object", []) };

        var ifExpr = new IrNode.If(
                new IrNode.BoolConst(true) { Type = ZType.Bool },
                setField,
                nullConst)
            { Type = ZType.Unit };

        var method = new IrObjectMethod("Apply", [], ZType.Unit, ifExpr);
        var classDecl = new IrNode.ClassDecl("Cfg", [], [],
            [new IrField("count", ZType.Int, IsMutable: true)],
            [method]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitIfWithMixedArmTypes_ValueOverall_UnitBranch()
    {
        // if (cond) null else (set! field value)
        // Overall type is Object — the Unit branch should push ldnull
        var setField = new IrNode.SetField("count", new IrNode.IntConst(1) { Type = ZType.Int })
            { Type = ZType.Unit };
        var nullConst = new IrNode.NullConst { Type = new ZType.ZNamedType("System.Object", []) };

        var ifExpr = new IrNode.If(
                new IrNode.BoolConst(true) { Type = ZType.Bool },
                nullConst,
                setField)
            { Type = new ZType.ZNamedType("System.Object", []) };

        var method = new IrObjectMethod("Apply", [],
            new ZType.ZNamedType("System.Object", []), ifExpr);
        var classDecl = new IrNode.ClassDecl("Cfg", [], [],
            [new IrField("count", ZType.Int, IsMutable: true)],
            [method]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMatchWithMixedArmTypes_SetFieldAndNull()
    {
        // match on Option-like union:
        //   (Some v) → set! field (Unit)
        //   None → null (Object)
        // Overall type Unit — reproduces the ApplyConfig pattern
        var optionType = new ZType.ZNamedType("Option", [ZType.Int]);

        var setField = new IrNode.SetField("value",
                new IrNode.Var("v") { Type = ZType.Int })
            { Type = ZType.Unit };

        var nullConst = new IrNode.NullConst { Type = new ZType.ZNamedType("System.Object", []) };

        var matchExpr = new IrNode.Match(
                new IrNode.UnionCaseNew("Option", "Some",
                        [new IrNode.IntConst(42) { Type = ZType.Int }])
                    { Type = optionType },
                [
                    new IrMatchArm(
                        new IrPattern.Constructor("Some", [new IrPattern.Variable("v")]),
                        setField),
                    new IrMatchArm(
                        new IrPattern.Wildcard(),
                        nullConst)
                ])
            { Type = ZType.Unit };

        var optionDecl = new IrNode.UnionDecl("Option", ["a"], [
            new IrUnionCase("Some", [new IrField("value", new ZType.ZNamedType("a", []))]),
            new IrUnionCase("None", [])
        ]);

        var method = new IrObjectMethod("ApplyConfig", [], ZType.Unit, matchExpr);
        var classDecl = new IrNode.ClassDecl("Effect", [], [],
            [new IrField("value", ZType.Int, IsMutable: true)],
            [method]);

        var seq = new IrNode.Seq([optionDecl, classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Async void-returning (bare Task) ────────────────────────────

    [Fact]
    public void AsyncVoidReturn_BareTaskType_EmitsStateMachine()
    {
        var computeAsync = new IrNode.FuncDef("compute-async",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.BinOp("+",
                    new IrNode.Var("x") { Type = ZType.Int },
                    new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskInt) };

        var doWork = new IrNode.FuncDef("do-work",
                [], ZType.Unit,
                new IrNode.Let("result",
                        new IrNode.Await(
                                new IrNode.Call(
                                    new IrNode.Var("compute-async")
                                        { Type = new ZType.ZFuncType([ZType.Int], TaskInt) },
                                    [new IrNode.IntConst(1) { Type = ZType.Int }]) { Type = TaskInt })
                            { Type = ZType.Int },
                        new IrNode.UnitConst { Type = ZType.Unit })
                    { Type = ZType.Unit },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskUnit) };

        var seq = new IrNode.Seq([computeAsync, doWork]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAsyncAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── InitializeLocals on class methods ───────────────────────────

    [Fact]
    public void EmitClassDecl_MethodWithLocals_EmitsSuccessfully()
    {
        var method = new IrObjectMethod("Compute", [new IrParam("x", ZType.Int)], ZType.Int,
            new IrNode.Let("temp",
                    new IrNode.BinOp("+",
                        new IrNode.Var("x") { Type = ZType.Int },
                        new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
                    new IrNode.BinOp("*",
                        new IrNode.Var("temp") { Type = ZType.Int },
                        new IrNode.IntConst(2) { Type = ZType.Int }) { Type = ZType.Int })
                { Type = ZType.Int });

        var classDecl = new IrNode.ClassDecl("Calculator", [], [],
            [], [method]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── NullConst for nullable types ────────────────────────────────

    [Fact]
    public void EmitNullConst_NullableReferenceType_EmitsSuccessfully()
    {
        var nullableString = new ZType.ZNullableType(ZType.String);
        var func = new IrNode.FuncDef("getNull", [],
                nullableString,
                new IrNode.NullConst { Type = nullableString },
                false)
            { Type = new ZType.ZFuncType([], nullableString) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitNullConst_NullableValueType_EmitsSuccessfully()
    {
        var nullableFloat = new ZType.ZNullableType(ZType.Float);
        var func = new IrNode.FuncDef("getNull", [],
                nullableFloat,
                new IrNode.NullConst { Type = nullableFloat },
                false)
            { Type = new ZType.ZFuncType([], nullableFloat) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Boxing value type to reference type ─────────────────────────

    [Fact]
    public void EmitClassMethod_ReturningBoxedValueType_EmitsSuccessfully()
    {
        var objectType = new ZType.ZNamedType("System.Object", []);
        var method = new IrObjectMethod("Box", [new IrParam("x", ZType.Int)],
            objectType,
            new IrNode.Var("x") { Type = ZType.Int });

        var classDecl = new IrNode.ClassDecl("Boxer", [], [],
            [], [method]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Static property fallback (e.g., Task.CompletedTask) ─────────

    [Fact]
    public void EmitClrCall_StaticProperty_EmitsSuccessfully()
    {
        var taskType = new ZType.ZNamedType("Task", []);
        var clrCall = new IrNode.ClrCall(
                "System.Threading.Tasks.Task", "CompletedTask", [])
            { Type = taskType };

        var func = new IrNode.FuncDef("GetCompletedTask", [],
                taskType,
                clrCall, false)
            { Type = new ZType.ZFuncType([], taskType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Generic union from imported module ──────────────────────────

    [Fact]
    public void EmitMatch_GenericUnionFromImportedModule_EmitsSuccessfully()
    {
        var optionIntType = new ZType.ZNamedType("Option", [ZType.Int]);

        var matchExpr = new IrNode.Match(
                new IrNode.Var("opt") { Type = optionIntType },
                [
                    new IrMatchArm(
                        new IrPattern.Constructor("Some", [new IrPattern.Variable("v")]),
                        new IrNode.Var("v") { Type = ZType.Int }),
                    new IrMatchArm(
                        new IrPattern.Wildcard(),
                        new IrNode.IntConst(0) { Type = ZType.Int })
                ])
            { Type = ZType.Int };

        var func = new IrNode.FuncDef("unwrap",
                [new IrParam("opt", optionIntType)], ZType.Int,
                matchExpr, false)
            { Type = new ZType.ZFuncType([optionIntType], ZType.Int) };

        var optionDecl = new IrNode.UnionDecl("Option", ["a"], [
            new IrUnionCase("Some", [new IrField("value", new ZType.ZNamedType("a", []))]),
            new IrUnionCase("None", [])
        ]);

        var modules = new List<(string ClassName, IReadOnlyList<IrNode> Definitions)>
        {
            ("OptionModule", new IrNode[] { optionDecl })
        };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass",
            importedModules: modules);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Out Parameter Method Calls ─────────────────────────────────────

    [Fact]
    public void EmitOutParamMethodCall_InstanceTryGetValue()
    {
        var mapType = new ZType.ZNamedType("Mutable-Map", [ZType.String, ZType.Int]);
        var tupleType = new ZType.ZNamedType("ValueTuple", [ZType.Bool, ZType.Int]);
        var outParams = new List<ClrInterop.OutParamInfo>
        {
            new(1, ZType.Int)
        };

        var param = new IrParam("dict", mapType);
        var func = new IrNode.FuncDef("tryGet", [param], tupleType,
                new IrNode.MethodCall(
                        new IrNode.Var("dict") { Type = mapType },
                        "TryGetValue",
                        [new IrNode.StringConst("key") { Type = ZType.String }],
                        false, false, OutParams: outParams)
                    { Type = tupleType },
                false)
            { Type = new ZType.ZFuncType([mapType], tupleType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitOutParamMethodCall_InstanceMethodNotFound()
    {
        var mapType = new ZType.ZNamedType("Mutable-Map", [ZType.String, ZType.Int]);
        var tupleType = new ZType.ZNamedType("ValueTuple", [ZType.Bool, ZType.Int]);
        var outParams = new List<ClrInterop.OutParamInfo>
        {
            new(1, ZType.Int)
        };

        var param = new IrParam("dict", mapType);
        var func = new IrNode.FuncDef("tryNonExistent", [param], tupleType,
                new IrNode.MethodCall(
                        new IrNode.Var("dict") { Type = mapType },
                        "NonExistentMethod",
                        [new IrNode.StringConst("key") { Type = ZType.String }],
                        false, false, OutParams: outParams)
                    { Type = tupleType },
                false)
            { Type = new ZType.ZFuncType([mapType], tupleType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        emitter.Emit(seq);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("with out parameters not found"));
    }

    [Fact]
    public void EmitOutParamStaticCall_IntTryParse()
    {
        var tupleType = new ZType.ZNamedType("ValueTuple", [ZType.Bool, ZType.Int]);
        var outParams = new List<ClrInterop.OutParamInfo>
        {
            new(1, ZType.Int)
        };

        var clrCall = new IrNode.ClrCall(
                "System.Int32", "TryParse",
                [new IrNode.StringConst("42") { Type = ZType.String }],
                OutParams: outParams)
            { Type = tupleType };

        var func = new IrNode.FuncDef("tryParse", [], tupleType, clrCall, false)
            { Type = new ZType.ZFuncType([], tupleType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitOutParamStaticCall_MethodNotFound()
    {
        var tupleType = new ZType.ZNamedType("ValueTuple", [ZType.Bool, ZType.Int]);
        var outParams = new List<ClrInterop.OutParamInfo>
        {
            new(1, ZType.Int)
        };

        var clrCall = new IrNode.ClrCall(
                "System.Int32", "NonExistent",
                [new IrNode.StringConst("42") { Type = ZType.String }],
                OutParams: outParams)
            { Type = tupleType };

        var func = new IrNode.FuncDef("tryNonExistent", [], tupleType, clrCall, false)
            { Type = new ZType.ZFuncType([], tupleType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        emitter.Emit(seq);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("with out parameters not found"));
    }
}
