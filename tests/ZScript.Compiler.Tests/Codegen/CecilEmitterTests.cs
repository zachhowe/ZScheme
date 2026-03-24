namespace ZScript.Compiler.Tests.Codegen;

using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;
using Xunit;

public class CecilEmitterTests
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
        var emitter = new CecilEmitter("TestAssembly", diag);
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
        var emitter = new CecilEmitter("TestAssembly", diag);
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
        var emitter = new CecilEmitter("TestAssembly", diag);
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

        var callFunc = new IrNode.FuncDef(
            "test",
            [],
            ZType.Int,
            new IrNode.Call(
                new IrNode.Var("add") { Type = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int) },
                [new IrNode.IntConst(1) { Type = ZType.Int }, new IrNode.IntConst(2) { Type = ZType.Int }])
            { Type = ZType.Int },
            false)
        { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([addFunc, callFunc]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new CecilEmitter("TestAssembly", diag);
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

        var func = new IrNode.FuncDef("test", [], ZType.Int, body, false)
        { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new CecilEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitRecordDecl()
    {
        var recordDecl = new IrNode.RecordDecl("Point", [],
        [
            new IrField("x", ZType.Int),
            new IrField("y", ZType.Int)
        ]);

        var seq = new IrNode.Seq([recordDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new CecilEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnionDecl()
    {
        var unionDecl = new IrNode.UnionDecl("Shape", [],
        [
            new IrUnionCase("Circle", [new IrField("radius", ZType.Int)]),
            new IrUnionCase("Rect", [new IrField("w", ZType.Int), new IrField("h", ZType.Int)])
        ]);

        var seq = new IrNode.Seq([unionDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new CecilEmitter("TestAssembly", diag);
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
        var emitter = new CecilEmitter("TestAssembly", diag);
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
        var emitter = new CecilEmitter("TestAssembly", diag);
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
        var emitter = new CecilEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitBoolOperations()
    {
        var func = new IrNode.FuncDef("test",
            [new IrParam("a", ZType.Bool), new IrParam("b", ZType.Bool)],
            ZType.Bool,
            new IrNode.BinOp("and",
                new IrNode.Var("a") { Type = ZType.Bool },
                new IrNode.Var("b") { Type = ZType.Bool })
            { Type = ZType.Bool },
            false)
        { Type = new ZType.ZFuncType([ZType.Bool, ZType.Bool], ZType.Bool) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new CecilEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
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
        var emitter = new CecilEmitter("TestAssembly", diag);
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
        var emitter = new CecilEmitter("TestAssembly", diag);
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
        var emitter = new CecilEmitter("TestAssembly", diag);
        emitter.Emit(seq);

        Assert.False(emitter.HasEntryPoint);
    }

    [Fact]
    public void EmitTopLevelLetBinding()
    {
        var let = new IrNode.Let("x",
            new IrNode.IntConst(42) { Type = ZType.Int },
            new IrNode.UnitConst() { Type = ZType.Unit })
        { Type = ZType.Unit };

        var func = new IrNode.FuncDef("getX", [], ZType.Int,
            new IrNode.Var("x") { Type = ZType.Int }, false)
        { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([let, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new CecilEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }
}
