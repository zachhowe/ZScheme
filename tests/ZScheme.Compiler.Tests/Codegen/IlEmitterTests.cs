using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
    private static readonly ZType TaskString = new ZType.ZNamedType("Task", [ZType.String]);

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
    public void EmitRecordDecl_RuntimeEquals_Works()
    {
        // Probe: confirms the existing record-class equality emission works at runtime
        // (the same code path that EmitStructEquality is modeled on).
        var record = new IrNode.RecordDecl("Pt", [], [
            new IrField("x", ZType.Int)
        ]);
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("RecEqualityProbe", diag, "TestClass");
        var bytes = emitter.Emit(new IrNode.Seq([record]) { Type = ZType.Unit });
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var asm = Assembly.Load(bytes!);
        var pt = asm.GetTypes().First(t => t.Name == "Pt");
        var ctor = pt.GetConstructor([typeof(int)])!;
        var a = ctor.Invoke([1]);
        var b = ctor.Invoke([1]);
        Assert.True(a!.Equals(b));
    }

    [Fact]
    public void EmitStructDecl_EmitsValueTypeWithBaseSystemValueType()
    {
        var structDecl = new IrNode.RecordDecl("Point", [], [
            new IrField("x", ZType.Int),
            new IrField("y", ZType.Int)
        ], IsValueType: true);

        var seq = new IrNode.Seq([structDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        var asm = Assembly.Load(bytes!);
        var pointType = asm.GetTypes().FirstOrDefault(t => t.Name == "Point");
        Assert.NotNull(pointType);
        // A real CLR struct: IsValueType is true and BaseType is System.ValueType.
        Assert.True(pointType.IsValueType);
        Assert.Equal(typeof(ValueType), pointType.BaseType);
        Assert.True(pointType.IsSealed);
        // Struct must NOT have <Clone>$ or EqualityContract — those belong to record class.
        Assert.Null(pointType.GetMethod("<Clone>$",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Null(pointType.GetProperty("EqualityContract",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void EmitStructDecl_HasStructuralEqualityMembers()
    {
        var structDecl = new IrNode.RecordDecl("Point", [], [
            new IrField("x", ZType.Int),
            new IrField("y", ZType.Int)
        ], IsValueType: true);

        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(new IrNode.Seq([structDecl]) { Type = ZType.Unit });
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var asm = Assembly.Load(bytes!);
        var pointType = asm.GetTypes().First(t => t.Name == "Point");
        Assert.NotNull(pointType.GetMethod("Equals", [pointType]));
        Assert.NotNull(pointType.GetMethod("Equals", [typeof(object)]));
        Assert.NotNull(pointType.GetMethod("GetHashCode", Type.EmptyTypes));
        Assert.NotNull(pointType.GetMethod("op_Equality", [pointType, pointType]));
        Assert.NotNull(pointType.GetMethod("op_Inequality", [pointType, pointType]));
    }

    [Fact]
    public void EmitStructDecl_ValueSemantics_Roundtrip()
    {
        // The whole point of structs: value copies don't share state. Constructing two
        // instances with the same fields must compare equal; mutating one local must not
        // affect the other.
        var structDecl = new IrNode.RecordDecl("Point", [], [
            new IrField("x", ZType.Int),
            new IrField("y", ZType.Int)
        ], IsValueType: true);

        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(new IrNode.Seq([structDecl]) { Type = ZType.Unit });
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var asm = Assembly.Load(bytes!);
        var pointType = asm.GetTypes().First(t => t.Name == "Point");
        var ctor = pointType.GetConstructor([typeof(int), typeof(int)])!;
        var a = ctor.Invoke([1, 2]);
        var b = ctor.Invoke([1, 2]);
        var c = ctor.Invoke([1, 3]);
        Assert.True(a!.Equals(b));
        Assert.False(a.Equals(c));
        Assert.Equal(a.GetHashCode(), b!.GetHashCode());
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

    [Fact]
    public void EmitMatchTuplePattern_LiteralElement_FailsThroughToNextArm()
    {
        // Regression: EmitTuplePatternTest used to silently skip every tuple element
        // that wasn't a Wildcard or Variable, so `(values 1 x)` matched the same set
        // of scrutinees as `(values _ x)`. With (5, 10), the buggy IL hit the first
        // arm and returned 100 instead of falling through to the wildcard's 999.
        // Repro: fuzzer seed 0x32b37a3c (case fuzz-failure-32b37a3c).
        var tupleType = new ZType.ZNamedType("ValueTuple", [ZType.Int, ZType.Int]);
        var scrutinee = new IrNode.TupleNew([
                new IrNode.IntConst(5) { Type = ZType.Int },
                new IrNode.IntConst(10) { Type = ZType.Int }
            ]) { Type = tupleType };

        var match = new IrNode.Match(scrutinee, [
            new IrMatchArm(
                new IrPattern.Tuple([new IrPattern.Literal(1), new IrPattern.Variable("x")]),
                new IrNode.IntConst(100) { Type = ZType.Int }),
            new IrMatchArm(
                new IrPattern.Tuple([new IrPattern.Literal(2), new IrPattern.Variable("x")]),
                new IrNode.IntConst(200) { Type = ZType.Int }),
            new IrMatchArm(
                new IrPattern.Wildcard(),
                new IrNode.IntConst(999) { Type = ZType.Int })
        ]) { Type = ZType.Int };

        var func = new IrNode.FuncDef("Pick", [], ZType.Int, match, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TuplePatLitAsm", diag, "TuplePatLitClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var asm = Assembly.Load(bytes!);
        var cls = asm.GetType("TuplePatLitAsm.TuplePatLitClass")!;
        var method = cls.GetMethod("Pick", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(999, method.Invoke(null, null));
    }

    [Fact]
    public void EmitMatchTuplePattern_LiteralElement_MatchesWhenEqual()
    {
        // Companion to EmitMatchTuplePattern_LiteralElement_FailsThroughToNextArm:
        // verifies that when the literal does match, the bound variable in the same
        // arm is still wired up correctly (the field load paths for Variable and
        // Literal share state in the rewritten loop).
        var tupleType = new ZType.ZNamedType("ValueTuple", [ZType.Int, ZType.Int]);
        var scrutinee = new IrNode.TupleNew([
                new IrNode.IntConst(2) { Type = ZType.Int },
                new IrNode.IntConst(77) { Type = ZType.Int }
            ]) { Type = tupleType };

        var match = new IrNode.Match(scrutinee, [
            new IrMatchArm(
                new IrPattern.Tuple([new IrPattern.Literal(1), new IrPattern.Variable("x")]),
                new IrNode.IntConst(100) { Type = ZType.Int }),
            new IrMatchArm(
                new IrPattern.Tuple([new IrPattern.Literal(2), new IrPattern.Variable("x")]),
                new IrNode.Var("x") { Type = ZType.Int }),
            new IrMatchArm(
                new IrPattern.Wildcard(),
                new IrNode.IntConst(999) { Type = ZType.Int })
        ]) { Type = ZType.Int };

        var func = new IrNode.FuncDef("Pick", [], ZType.Int, match, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TuplePatBindAsm", diag, "TuplePatBindClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var asm = Assembly.Load(bytes!);
        var cls = asm.GetType("TuplePatBindAsm.TuplePatBindClass")!;
        var method = cls.GetMethod("Pick", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(77, method.Invoke(null, null));
    }

    [Fact]
    public void EmitMatchTuplePattern_NestedTupleWithLiteral_FailsThrough()
    {
        // Nested-pattern variant: the bugged loop dropped *all* non-Variable/Wildcard
        // sub-patterns, including nested tuple patterns. Without recursion, the inner
        // literal `0` was ignored and the first arm matched on (5, (1, 2)).
        var innerTupleType = new ZType.ZNamedType("ValueTuple", [ZType.Int, ZType.Int]);
        var outerTupleType = new ZType.ZNamedType("ValueTuple", [ZType.Int, innerTupleType]);

        var scrutinee = new IrNode.TupleNew([
                new IrNode.IntConst(5) { Type = ZType.Int },
                new IrNode.TupleNew([
                    new IrNode.IntConst(1) { Type = ZType.Int },
                    new IrNode.IntConst(2) { Type = ZType.Int }
                ]) { Type = innerTupleType }
            ]) { Type = outerTupleType };

        var match = new IrNode.Match(scrutinee, [
            new IrMatchArm(
                new IrPattern.Tuple([
                    new IrPattern.Variable("a"),
                    new IrPattern.Tuple([new IrPattern.Literal(0), new IrPattern.Variable("b")])
                ]),
                new IrNode.IntConst(100) { Type = ZType.Int }),
            new IrMatchArm(
                new IrPattern.Wildcard(),
                new IrNode.IntConst(999) { Type = ZType.Int })
        ]) { Type = ZType.Int };

        var func = new IrNode.FuncDef("Pick", [], ZType.Int, match, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("NestedTuplePatAsm", diag, "NestedTuplePatClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var asm = Assembly.Load(bytes!);
        var cls = asm.GetType("NestedTuplePatAsm.NestedTuplePatClass")!;
        var method = cls.GetMethod("Pick", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(999, method.Invoke(null, null));
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
    public void EmitGenericRecordNew_UsesClosedGenericCtor()
    {
        // Regression: EmitRecordNew used to emit `newobj Pair::.ctor(!0)` (the open
        // generic ctor MethodDefinition) instead of `newobj Pair<int32>::.ctor(!0)`.
        // ilverify rejected the result as a bare `Pair` reference where `Pair<int32>`
        // was expected, and the JIT throws InvalidProgramException when the function
        // is actually invoked. Asserting that invocation succeeds locks in the fix.
        var recordDecl = new IrNode.RecordDecl("Pair", ["a"],
        [
            new IrField("first", new ZType.ZNamedType("a", [])),
            new IrField("second", new ZType.ZNamedType("a", []))
        ]);

        var pairOfInt = new ZType.ZNamedType("Pair", [ZType.Int]);
        var func = new IrNode.FuncDef("first", [], ZType.Int,
                new IrNode.FieldGet(
                    new IrNode.RecordNew("Pair",
                    [
                        ("first", new IrNode.IntConst(7) { Type = ZType.Int }),
                        ("second", new IrNode.IntConst(13) { Type = ZType.Int })
                    ]) { Type = pairOfInt },
                    "first") { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([recordDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("ClosedGenericCtorAsm", diag, "ClosedGenericCtorClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var asm = Assembly.Load(bytes!);
        var cls = asm.GetType("ClosedGenericCtorAsm.ClosedGenericCtorClass")!;
        var method = cls.GetMethod("First", BindingFlags.Public | BindingFlags.Static)!;
        // If the ctor reference is on the open `Pair`, the JIT raises
        // InvalidProgramException when First is JIT-compiled.
        Assert.Equal(7, method.Invoke(null, null));
    }

    [Fact]
    public void EmitStructFieldGet_ViaPropertyMethodCall_UsesCallNotCallvirt()
    {
        // Regression: property access on a value-type record was lowered to IrNode.MethodCall
        // (slash syntax: ClassName/field) and EmitMethodCall queried isValueType through
        // ResolveClrType. For user-defined types still being compiled, ResolveClrType falls
        // back to System.Object (not loaded into the AppDomain yet), so isValueType was false
        // and the emitter produced `callvirt` plus a value (not a managed pointer) on the
        // stack. ilverify rejected this with "Callvirt on a value type method" / "expected
        // address of T" errors, and the JIT raised InvalidProgramException at first invocation.
        var structDecl = new IrNode.RecordDecl("FRec", [],
        [
            new IrField("first", ZType.Int),
            new IrField("second", ZType.Int)
        ], IsValueType: true);

        var frecType = new ZType.ZNamedType("FRec", []);
        var func = new IrNode.FuncDef("firstof", [], ZType.Int,
                new IrNode.MethodCall(
                    new IrNode.RecordNew("FRec",
                    [
                        ("first", new IrNode.IntConst(7) { Type = ZType.Int }),
                        ("second", new IrNode.IntConst(13) { Type = ZType.Int })
                    ]) { Type = frecType },
                    "first", [], IsProperty: true, IsIndexer: false) { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([structDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("StructFieldCallAsm", diag, "StructFieldCallClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var asm = Assembly.Load(bytes!);
        var cls = asm.GetType("StructFieldCallAsm.StructFieldCallClass")!;
        var method = cls.GetMethod("Firstof", BindingFlags.Public | BindingFlags.Static)!;
        // Pre-fix: JIT throws InvalidProgramException because of the callvirt-on-valuetype pair.
        Assert.Equal(7, method.Invoke(null, null));
    }

    [Fact]
    public void EmitGenericStructFieldGet_ViaPropertyMethodCall_UsesValueTypeGenericInstance()
    {
        // Regression: the generic-instance signature for a struct property access was created
        // with MakeGenericInstanceType(false, ...) — encoding the receiver as ELEMENT_TYPE_CLASS
        // even though FRec<int,int> is a value type. That mismatch surfaced as ilverify
        // "Unexpected type on the stack" errors and at runtime as InvalidProgramException.
        var structDecl = new IrNode.RecordDecl("FRec", ["a", "b"],
        [
            new IrField("first", new ZType.ZNamedType("a", [])),
            new IrField("second", new ZType.ZNamedType("b", []))
        ], IsValueType: true);

        var frecOfIntInt = new ZType.ZNamedType("FRec", [ZType.Int, ZType.Int]);
        var func = new IrNode.FuncDef("firstof", [], ZType.Int,
                new IrNode.MethodCall(
                    new IrNode.RecordNew("FRec",
                    [
                        ("first", new IrNode.IntConst(42) { Type = ZType.Int }),
                        ("second", new IrNode.IntConst(99) { Type = ZType.Int })
                    ]) { Type = frecOfIntInt },
                    "first", [], IsProperty: true, IsIndexer: false) { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([structDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("GenericStructFieldAsm", diag, "GenericStructFieldClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var asm = Assembly.Load(bytes!);
        var cls = asm.GetType("GenericStructFieldAsm.GenericStructFieldClass")!;
        var method = cls.GetMethod("Firstof", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(42, method.Invoke(null, null));
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
    public void EmitObjectExpr_WithBaseClassAndSuperArgsReferencingOuterParam()
    {
        // Regression: super args that reference outer-scope parameters used
        // to crash EmitLoadVar with ArgumentOutOfRangeException because the
        // anonymous object's zero-arg constructor was being indexed with the
        // enclosing method's parameter positions. The fix threads the free
        // vars through the constructor as real parameters so EmitLoadVar can
        // resolve them against ctor.Parameters.
        var baseDecl = new IrNode.ClassDecl("Animal", [], [],
            [new IrField("name", ZType.String), new IrField("sound", ZType.String)],
            [],
            true);

        var ctor = new IrConstructor(
            [],
            [
                new IrNode.Var("n") { Type = ZType.String },
                new IrNode.StringConst("unknown") { Type = ZType.String }
            ],
            [],
            []);

        var objectExpr = new IrNode.ObjectExpr(
                [],
                [],
                "Animal",
                ctor)
            { Type = new ZType.ZNamedType("Animal", []) };

        var func = new IrNode.FuncDef("makeAnimal",
                [new IrParam("n", ZType.String)],
                new ZType.ZNamedType("Animal", []),
                objectExpr, false)
            { Type = new ZType.ZFuncType([ZType.String], new ZType.ZNamedType("Animal", [])) };

        var seq = new IrNode.Seq([baseDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitObjectExpr_MethodInvokesCapturedDelegateParam()
    {
        // Regression: a function-typed parameter of the enclosing function that
        // gets captured by an `object` expression's method body must be
        // resolvable as a delegate-invocation call site inside that method.
        // Before the fix, EmitCall only consulted outerParams / locals /
        // _staticFields / _currentClassMethods to dispatch a Call(Var) target;
        // captured class fields were never checked, so a body like
        // `(define (m) (f x))` — where `f : (Fn [Int] Int)` and `x : Int` were
        // both captured from the enclosing define — failed IL emission with
        // "Function 'f' not found for AsmResolver IL emission".
        // Discovered by the fuzzer (seed a86c7c76, case ab34b09e).
        var ifaceDecl = new IrNode.InterfaceDecl("IFoo", [], [],
            [new IrInterfaceMethodSignature("Call", [], ZType.Int)]);

        var fnIntInt = new ZType.ZFuncType([ZType.Int], ZType.Int);

        var objectExpr = new IrNode.ObjectExpr(
                ["IFoo"],
                [
                    new IrObjectMethod("Call", [], ZType.Int,
                        new IrNode.Call(
                                new IrNode.Var("f") { Type = fnIntInt },
                                [new IrNode.Var("x") { Type = ZType.Int }])
                            { Type = ZType.Int })
                ])
            { Type = new ZType.ZNamedType("IFoo", []) };

        var func = new IrNode.FuncDef("makeObj",
                [new IrParam("f", fnIntInt), new IrParam("x", ZType.Int)],
                new ZType.ZNamedType("IFoo", []),
                objectExpr, false)
            { Type = new ZType.ZFuncType([fnIntInt, ZType.Int], new ZType.ZNamedType("IFoo", [])) };

        var seq = new IrNode.Seq([ifaceDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // Verify the synthesized object class actually exists in the emitted
        // metadata, so the fix can't regress to silently dropping the
        // capture machinery while still passing the diagnostics check.
        using var peStream = new MemoryStream(bytes!);
        using var peReader = new PEReader(peStream);
        var mdReader = peReader.GetMetadataReader();
        var typeNames = mdReader.TypeDefinitions
            .Select(h => mdReader.GetString(mdReader.GetTypeDefinition(h).Name))
            .ToList();
        Assert.Contains(typeNames, n => n.StartsWith("<>__Object_"));
    }

    [Fact]
    public void EmitObjectExpr_MethodInvokesCapturedDelegateLocal()
    {
        // Companion to MethodInvokesCapturedDelegateParam: when the captured
        // delegate originates from a let-binding (local) rather than a
        // function parameter, the same field-dispatch path in EmitCall must
        // fire. Constructed as a Let that binds `f` to the enclosing
        // function's parameter and then returns an object whose method
        // invokes `f` — the closure analysis sees `f` as a free local in
        // the object's body and threads it through as a capture field.
        var ifaceDecl = new IrNode.InterfaceDecl("IFoo", [], [],
            [new IrInterfaceMethodSignature("Call", [], ZType.Int)]);

        var fnIntInt = new ZType.ZFuncType([ZType.Int], ZType.Int);

        var objectExpr = new IrNode.ObjectExpr(
                ["IFoo"],
                [
                    new IrObjectMethod("Call", [], ZType.Int,
                        new IrNode.Call(
                                new IrNode.Var("f") { Type = fnIntInt },
                                [new IrNode.IntConst(7) { Type = ZType.Int }])
                            { Type = ZType.Int })
                ])
            { Type = new ZType.ZNamedType("IFoo", []) };

        var letBody = new IrNode.Let("f",
                new IrNode.Var("g") { Type = fnIntInt },
                objectExpr)
            { Type = new ZType.ZNamedType("IFoo", []) };

        var func = new IrNode.FuncDef("makeObj",
                [new IrParam("g", fnIntInt)],
                new ZType.ZNamedType("IFoo", []),
                letBody, false)
            { Type = new ZType.ZFuncType([fnIntInt], new ZType.ZNamedType("IFoo", [])) };

        var seq = new IrNode.Seq([ifaceDecl, func]) { Type = ZType.Unit };
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
    public void AsyncNestedAwaitInsideArg_EmitsStateMachine()
    {
        // Regression: AsyncStateMachineAnalyzer.CollectInfo's Await case did not
        // recurse into the awaited expression, so a nested `(await (g (await (g 1))))`
        // counted as one await point but EmitMoveNextAwait visited two. The second
        // emit's AwaiterFields[1] lookup threw KeyNotFoundException. Surfaced by the
        // fuzzer (seed 0x73fe9f16).
        var fnTy = new ZType.ZFuncType([ZType.Int], TaskInt);

        var computeAsync = new IrNode.FuncDef("g",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.Var("x") { Type = ZType.Int },
                false, IsAsync: true)
            { Type = fnTy };

        // (await (g (await (g 1)))): inner Await is the argument expression of the outer.
        var innerAwait = new IrNode.Await(
                new IrNode.Call(
                    new IrNode.Var("g") { Type = fnTy },
                    [new IrNode.IntConst(1) { Type = ZType.Int }]) { Type = TaskInt })
            { Type = ZType.Int };
        var outerAwait = new IrNode.Await(
                new IrNode.Call(
                    new IrNode.Var("g") { Type = fnTy },
                    [innerAwait]) { Type = TaskInt })
            { Type = ZType.Int };

        var f = new IrNode.FuncDef("f", [], ZType.Int, outerAwait, false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskInt) };

        var seq = new IrNode.Seq([computeAsync, f]) { Type = ZType.Unit };
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
        var asm = Assembly.Load(bytes!);
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
        var asm = Assembly.Load(bytes!);
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

    // Regression: the IL emitter previously placed an orphan `nop` between
    // consecutive catch handlers (each handler closed with its own `nop` and
    // the next opened with a fresh `nop`). The CLR requires catch handlers
    // for the same protected region to be contiguous in the exception table,
    // so JIT-compiling the method threw InvalidProgramException at runtime.
    // Found via the fuzzer (seed 0x00000539, case 0x40407949).
    [Fact]
    public void EmitWithHandlers_MultipleClauses_HandlerRegionsAreContiguous()
    {
        var withHandlers = new IrNode.WithHandlers(
                new IrNode.IntConst(99) { Type = ZType.Int },
                [
                    new IrHandlerClause("System.ArgumentException", "x",
                        new IrNode.IntConst(17) { Type = ZType.Int }),
                    new IrHandlerClause("System.Exception", "y",
                        new IrNode.IntConst(18) { Type = ZType.Int })
                ])
            { Type = ZType.Int };

        var func = new IrNode.FuncDef("MultiCatch", [], ZType.Int, withHandlers, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("MultiCatchAsm", diag, "MultiCatchClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // Inspect the exception table and check that the handlers abut:
        // for catch handlers over the same try region, handler N's end offset
        // must equal handler N+1's start offset. A gap of even one byte
        // between them makes the CLR reject the method at JIT time.
        using var peStream = new MemoryStream(bytes!);
        using var peReader = new PEReader(peStream);
        var mdReader = peReader.GetMetadataReader();

        MethodBodyBlock? body = null;
        foreach (var typeHandle in mdReader.TypeDefinitions)
        {
            var typeDef = mdReader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in typeDef.GetMethods())
            {
                var method = mdReader.GetMethodDefinition(methodHandle);
                if (mdReader.GetString(method.Name) != "MultiCatch") continue;
                body = peReader.GetMethodBody(method.RelativeVirtualAddress);
                break;
            }
            if (body is not null) break;
        }
        Assert.NotNull(body);

        Assert.Equal(2, body!.ExceptionRegions.Length);
        var r0 = body.ExceptionRegions[0];
        var r1 = body.ExceptionRegions[1];
        Assert.Equal(r0.TryOffset, r1.TryOffset);
        Assert.Equal(r0.TryLength, r1.TryLength);
        var r0HandlerEnd = r0.HandlerOffset + r0.HandlerLength;
        Assert.Equal(r0HandlerEnd, r1.HandlerOffset);

        // End-to-end: invoking the method must not raise InvalidProgramException.
        var asm = Assembly.Load(bytes!);
        var cls = asm.GetType("MultiCatchAsm.MultiCatchClass")!;
        var mi = cls.GetMethod("MultiCatch", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(99, mi.Invoke(null, null));
    }

    // Regression: three catch clauses stress the "stitch previous handler's
    // end to the next handler's start" logic beyond the two-handler case. The
    // middle handler must both receive a stitched start and provide a
    // stitched end for the handler that follows it.
    [Fact]
    public void EmitWithHandlers_ThreeClauses_HandlerRegionsAreContiguous()
    {
        var withHandlers = new IrNode.WithHandlers(
                new IrNode.IntConst(1) { Type = ZType.Int },
                [
                    new IrHandlerClause("System.ArgumentException", "x",
                        new IrNode.IntConst(2) { Type = ZType.Int }),
                    new IrHandlerClause("System.InvalidOperationException", "y",
                        new IrNode.IntConst(3) { Type = ZType.Int }),
                    new IrHandlerClause("System.Exception", "z",
                        new IrNode.IntConst(4) { Type = ZType.Int })
                ])
            { Type = ZType.Int };

        var func = new IrNode.FuncDef("TripleCatch", [], ZType.Int, withHandlers, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TripleCatchAsm", diag, "TripleCatchClass");
        var bytes = emitter.Emit(seq);

        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.NotNull(bytes);

        using var peStream = new MemoryStream(bytes!);
        using var peReader = new PEReader(peStream);
        var mdReader = peReader.GetMetadataReader();

        MethodBodyBlock? body = null;
        foreach (var typeHandle in mdReader.TypeDefinitions)
        {
            var typeDef = mdReader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in typeDef.GetMethods())
            {
                var method = mdReader.GetMethodDefinition(methodHandle);
                if (mdReader.GetString(method.Name) != "TripleCatch") continue;
                body = peReader.GetMethodBody(method.RelativeVirtualAddress);
                break;
            }
            if (body is not null) break;
        }
        Assert.NotNull(body);
        Assert.Equal(3, body!.ExceptionRegions.Length);
        for (var i = 1; i < body.ExceptionRegions.Length; i++)
        {
            var prev = body.ExceptionRegions[i - 1];
            var cur = body.ExceptionRegions[i];
            Assert.Equal(prev.HandlerOffset + prev.HandlerLength, cur.HandlerOffset);
        }

        var asm = Assembly.Load(bytes!);
        var cls = asm.GetType("TripleCatchAsm.TripleCatchClass")!;
        var mi = cls.GetMethod("TripleCatch", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(1, mi.Invoke(null, null));
    }

    // Regression: when a method body had more than 20 catch handlers,
    // AsmResolver chose the "tiny" CIL extra-section format for the EH table
    // even though the section's 1-byte DataSize field can only encode
    // 4 + 12*N <= 255 → N <= 20 entries. With N >= 22, the size byte wrapped
    // mod 256 and the runtime read back zero EH clauses; `leave` instructions
    // then escaped their (now non-existent) protected regions and exceptions
    // propagated past `with-handlers` as if the catch were absent.
    // Discovered via the diff-exec fuzzer (case 0xdf0d8726) where one F0 had
    // 22 catch handlers — IL threw "fuzz" while the C# backend returned 90.
    // The IL emitter now pads one handler's TryStart..TryEnd region to span
    // >= 255 bytes when EH count > 20, forcing AsmResolver to emit fat
    // format and preserving every handler.
    [Fact]
    public void EmitWithHandlers_ManyClausesStillCatchAtRuntime()
    {
        // Build (compute) = sum_{i=1..N} (with-handlers ([System.Exception _] i)
        //                                   (raise (new System.Exception "fuzz")))
        // where each with-handlers throws inside its try and catches with
        // value i. Correct behavior: compute returns N*(N+1)/2.
        const int N = 22;

        IrNode raiseFuzz()
        {
            var ctor = new IrNode.ClrNew("System.Exception", [],
                [new IrNode.StringConst("fuzz") { Type = ZType.String }])
            { Type = ZType.Unit };
            return new IrNode.Throw(ctor) { Type = ZType.Int };
        }

        IrNode whN(int i) =>
            new IrNode.WithHandlers(
                    raiseFuzz(),
                    [
                        new IrHandlerClause("System.Exception", "_",
                            new IrNode.IntConst(i) { Type = ZType.Int })
                    ])
                { Type = ZType.Int };

        IrNode body = whN(N);
        for (var i = N - 1; i >= 1; i--)
            body = new IrNode.BinOp("+", whN(i), body) { Type = ZType.Int };

        var func = new IrNode.FuncDef("Compute", [], ZType.Int, body, false)
            { Type = new ZType.ZFuncType([], ZType.Int) };
        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };

        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("ManyHandlersAsm", diag, "ManyHandlersClass");
        var bytes = emitter.Emit(seq);

        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.NotNull(bytes);

        // Verify that all N user-emitted EH clauses survived serialization. The padding
        // workaround may add extra entries; the count must be at least N.
        using var peStream = new MemoryStream(bytes!);
        using var peReader = new PEReader(peStream);
        var mdReader = peReader.GetMetadataReader();

        MethodBodyBlock? methodBody = null;
        foreach (var typeHandle in mdReader.TypeDefinitions)
        {
            var typeDef = mdReader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in typeDef.GetMethods())
            {
                var method = mdReader.GetMethodDefinition(methodHandle);
                if (mdReader.GetString(method.Name) != "Compute") continue;
                methodBody = peReader.GetMethodBody(method.RelativeVirtualAddress);
                break;
            }
            if (methodBody is not null) break;
        }
        Assert.NotNull(methodBody);
        Assert.True(methodBody!.ExceptionRegions.Length >= N,
            $"expected at least {N} EH regions, got {methodBody.ExceptionRegions.Length}");

        // End-to-end: invoking Compute must catch every throw and return the sum.
        var asm = Assembly.Load(bytes!);
        var cls = asm.GetType("ManyHandlersAsm.ManyHandlersClass")!;
        var mi = cls.GetMethod("Compute", BindingFlags.Public | BindingFlags.Static)!;
        var expected = N * (N + 1) / 2;
        Assert.Equal(expected, mi.Invoke(null, null));
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

    // ─── Async without await: non-generic Task and Task<T> ──────────

    [Fact]
    public void AsyncWithoutAwait_NonGenericTask_UsesCompletedTask()
    {
        var func = new IrNode.FuncDef("do-nothing",
                [new IrParam("x", ZType.Int)], ZType.Unit,
                new IrNode.BinOp("+",
                    new IrNode.Var("x") { Type = ZType.Int },
                    new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([ZType.Int], TaskUnit) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAsyncAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void AsyncWithoutAwait_TaskOfString_UsesFromResult()
    {
        var func = new IrNode.FuncDef("greet",
                [], ZType.String,
                new IrNode.StringConst("hello") { Type = ZType.String },
                false, IsAsync: true)
            { Type = new ZType.ZFuncType([], TaskString) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAsyncAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void AsyncClassMethod_WithoutAwait_NonGenericTask_Emits()
    {
        var method = new IrObjectMethod("do-work", [new IrParam("x", ZType.Int)],
            new ZType.ZNamedType("Task", []),
            new IrNode.BinOp("+",
                new IrNode.Var("x") { Type = ZType.Int },
                new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int },
            IsAsync: true);

        var classDecl = new IrNode.ClassDecl("Worker", [], [], [], [method]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAsyncAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void AsyncClassMethod_WithoutAwait_TaskOfString_Emits()
    {
        var method = new IrObjectMethod("greet", [],
            new ZType.ZNamedType("Task", [ZType.String]),
            new IrNode.StringConst("hello") { Type = ZType.String },
            IsAsync: true);

        var classDecl = new IrNode.ClassDecl("Greeter", [], [], [], [method]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAsyncAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    // ─── Class method sibling and module-level calls ─────────────────

    [Fact]
    public void EmitClassDecl_SiblingMethodCall_Emits()
    {
        var doubleMethod = new IrObjectMethod("double",
            [new IrParam("x", ZType.Int)], ZType.Int,
            new IrNode.BinOp("+",
                new IrNode.Var("x") { Type = ZType.Int },
                new IrNode.Var("x") { Type = ZType.Int }) { Type = ZType.Int });

        var quadrupleMethod = new IrObjectMethod("quadruple",
            [new IrParam("x", ZType.Int)], ZType.Int,
            new IrNode.Call(
                new IrNode.Var("double") { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) },
                [new IrNode.Call(
                    new IrNode.Var("double") { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) },
                    [new IrNode.Var("x") { Type = ZType.Int }]) { Type = ZType.Int }])
            { Type = ZType.Int });

        var classDecl = new IrNode.ClassDecl("MathHelper", [], [], [],
            [doubleMethod, quadrupleMethod]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl_RecursiveMethodCall_Emits()
    {
        var countdownMethod = new IrObjectMethod("countdown",
            [new IrParam("n", ZType.Int)], ZType.Int,
            new IrNode.If(
                new IrNode.BinOp("=",
                    new IrNode.Var("n") { Type = ZType.Int },
                    new IrNode.IntConst(0) { Type = ZType.Int }) { Type = ZType.Bool },
                new IrNode.IntConst(0) { Type = ZType.Int },
                new IrNode.Call(
                    new IrNode.Var("countdown") { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) },
                    [new IrNode.BinOp("-",
                        new IrNode.Var("n") { Type = ZType.Int },
                        new IrNode.IntConst(1) { Type = ZType.Int }) { Type = ZType.Int }])
                { Type = ZType.Int })
            { Type = ZType.Int });

        var classDecl = new IrNode.ClassDecl("Counter", [], [], [],
            [countdownMethod]);

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl_MethodCallsModuleLevelFunc_Emits()
    {
        var helperFunc = new IrNode.FuncDef("helper",
                [new IrParam("x", ZType.Int)], ZType.Int,
                new IrNode.BinOp("+",
                    new IrNode.Var("x") { Type = ZType.Int },
                    new IrNode.IntConst(10) { Type = ZType.Int }) { Type = ZType.Int },
                false)
            { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) };

        var method = new IrObjectMethod("compute",
            [new IrParam("x", ZType.Int)], ZType.Int,
            new IrNode.Call(
                new IrNode.Var("helper") { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) },
                [new IrNode.Var("x") { Type = ZType.Int }]) { Type = ZType.Int });

        var classDecl = new IrNode.ClassDecl("Worker", [], [], [], [method]);

        var seq = new IrNode.Seq([helperFunc, classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, "TestClass");
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }
}
