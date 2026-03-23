namespace ZScript.Compiler.Tests.Codegen;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;
using Xunit;

public class IlEmitterTests
{
    private static readonly ZType ErrorInfoType = new ZType.ZNamedType("ErrorInfo", []);
    private static readonly ZType ResultIntErrorInfo = new ZType.ZNamedType("Result", [ZType.Int, ErrorInfoType]);
    private static readonly ZType OptionInt = new ZType.ZNamedType("Option", [ZType.Int]);

    /// <summary>
    /// Stdlib type declarations needed by tests that use Option/Result/ErrorInfo.
    /// </summary>
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
        var emitter = new IlEmitter("TestAssembly", diag);
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
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
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
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void EmittedAssemblyReferencesSystemRuntimeNotPrivateCoreLib()
    {
        // Build a minimal executable so we get an assembly with references
        var clrCall = new IrNode.ClrCall(
            "System.Console", "WriteLine",
            [new IrNode.StringConst("hello") { Type = ZType.String }])
        { Type = ZType.Unit };

        var seq = new IrNode.Seq([clrCall]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // Inspect the PE metadata to verify assembly references
        using var peReader = new PEReader(new MemoryStream(bytes));
        var metadataReader = peReader.GetMetadataReader();

        var refNames = new List<string>();
        foreach (var refHandle in metadataReader.AssemblyReferences)
        {
            var asmRef = metadataReader.GetAssemblyReference(refHandle);
            refNames.Add(metadataReader.GetString(asmRef.Name));
        }

        Assert.Contains("System.Console", refNames);
    }

    [Fact]
    public void EmitCallToUserDefinedFunction()
    {
        // Define add(x, y) = x + y
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

        // Call add(1, 2) from main
        var callNode = new IrNode.Call(
            new IrNode.Var("add") { Type = new ZType.ZFuncType([ZType.Int, ZType.Int], ZType.Int) },
            [new IrNode.IntConst(1) { Type = ZType.Int }, new IrNode.IntConst(2) { Type = ZType.Int }])
        { Type = ZType.Int };

        var seq = new IrNode.Seq([addFunc, callNode]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMatchOnOption()
    {
        // match opt { Some(v) => v, None => 0 }
        var matchNode = new IrNode.Match(
            new IrNode.Var("opt") { Type = OptionInt },
            [
                new IrMatchArm(
                    new IrPattern.Constructor("Some", [new IrPattern.Variable("v")]),
                    new IrNode.Var("v") { Type = ZType.Int }),
                new IrMatchArm(
                    new IrPattern.Constructor("None", []),
                    new IrNode.IntConst(0) { Type = ZType.Int })
            ])
        { Type = ZType.Int };

        var func = new IrNode.FuncDef("Unwrap", [new IrParam("opt", OptionInt)],
            ZType.Int, matchNode, false)
        { Type = new ZType.ZFuncType([OptionInt], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, importedModules: StdlibModules);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMatchWithWildcard()
    {
        // match x { "alice" => 1, _ => 0 }
        var matchNode = new IrNode.Match(
            new IrNode.Var("x") { Type = ZType.String },
            [
                new IrMatchArm(
                    new IrPattern.Literal("alice"),
                    new IrNode.IntConst(1) { Type = ZType.Int }),
                new IrMatchArm(
                    new IrPattern.Wildcard(),
                    new IrNode.IntConst(0) { Type = ZType.Int })
            ])
        { Type = ZType.Int };

        var func = new IrNode.FuncDef("Lookup", [new IrParam("x", ZType.String)],
            ZType.Int, matchNode, false)
        { Type = new ZType.ZFuncType([ZType.String], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitTryCatch()
    {
        // (catch (some-clr-call x)) -> Result<Int, ErrorInfo>
        var clrCall = new IrNode.ClrCall(
            "System.Int32", "Parse",
            [new IrNode.Var("s") { Type = ZType.String }])
        { Type = ZType.Int };

        var tryCatch = new IrNode.TryCatch(clrCall)
        { Type = ResultIntErrorInfo };

        var func = new IrNode.FuncDef("SafeParse", [new IrParam("s", ZType.String)],
            ResultIntErrorInfo, tryCatch, false)
        { Type = new ZType.ZFuncType([ZType.String], ResultIntErrorInfo) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, importedModules: StdlibModules);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMatchOnResult()
    {
        // match r { Ok(v) => v, Err(e) => 0 }
        var matchNode = new IrNode.Match(
            new IrNode.Var("r") { Type = ResultIntErrorInfo },
            [
                new IrMatchArm(
                    new IrPattern.Constructor("Ok", [new IrPattern.Variable("v")]),
                    new IrNode.Var("v") { Type = ZType.Int }),
                new IrMatchArm(
                    new IrPattern.Constructor("Err", [new IrPattern.Variable("e")]),
                    new IrNode.IntConst(0) { Type = ZType.Int })
            ])
        { Type = ZType.Int };

        var func = new IrNode.FuncDef("UnwrapResult", [new IrParam("r", ResultIntErrorInfo)],
            ZType.Int, matchNode, false)
        { Type = new ZType.ZFuncType([ResultIntErrorInfo], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, importedModules: StdlibModules);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitFloatConstant()
    {
        var func = new IrNode.FuncDef("GetPi",
            [],
            ZType.Float,
            new IrNode.FloatConst(3.14f) { Type = ZType.Float },
            false)
        { Type = new ZType.ZFuncType([], ZType.Float) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnitConstant()
    {
        var func = new IrNode.FuncDef("DoNothing",
            [],
            ZType.Unit,
            new IrNode.UnitConst() { Type = ZType.Unit },
            false)
        { Type = new ZType.ZFuncType([], ZType.Unit) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnaryNot()
    {
        var func = new IrNode.FuncDef("Negate",
            [new IrParam("b", ZType.Bool)],
            ZType.Bool,
            new IrNode.UnaryOp("not",
                new IrNode.Var("b") { Type = ZType.Bool })
            { Type = ZType.Bool },
            false)
        { Type = new ZType.ZFuncType([ZType.Bool], ZType.Bool) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitLetBinding()
    {
        // let y = x * 2 in y
        var body = new IrNode.Let("y",
            new IrNode.BinOp("*",
                new IrNode.Var("x") { Type = ZType.Int },
                new IrNode.IntConst(2) { Type = ZType.Int })
            { Type = ZType.Int },
            new IrNode.Var("y") { Type = ZType.Int })
        { Type = ZType.Int };

        var func = new IrNode.FuncDef("Double",
            [new IrParam("x", ZType.Int)],
            ZType.Int, body, false)
        { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitRecordDeclAndNew()
    {
        var pointType = new ZType.ZNamedType("Point", []);

        var recordDecl = new IrNode.RecordDecl("Point", [], [
            new IrField("x", ZType.Int),
            new IrField("y", ZType.Int)
        ]) { Type = ZType.Unit };

        var func = new IrNode.FuncDef("MakePoint",
            [],
            pointType,
            new IrNode.RecordNew("Point", [
                ("x", new IrNode.IntConst(1) { Type = ZType.Int }),
                ("y", new IrNode.IntConst(2) { Type = ZType.Int })
            ]) { Type = pointType },
            false)
        { Type = new ZType.ZFuncType([], pointType) };

        var seq = new IrNode.Seq([recordDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitFieldGet()
    {
        var pointType = new ZType.ZNamedType("Point", []);

        var recordDecl = new IrNode.RecordDecl("Point", [], [
            new IrField("x", ZType.Int),
            new IrField("y", ZType.Int)
        ]) { Type = ZType.Unit };

        var func = new IrNode.FuncDef("GetX",
            [new IrParam("p", pointType)],
            ZType.Int,
            new IrNode.FieldGet(
                new IrNode.Var("p") { Type = pointType },
                "x")
            { Type = ZType.Int },
            false)
        { Type = new ZType.ZFuncType([pointType], ZType.Int) };

        var seq = new IrNode.Seq([recordDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitUnionDeclAndCaseNew()
    {
        var shapeType = new ZType.ZNamedType("Shape", []);

        var unionDecl = new IrNode.UnionDecl("Shape", [], [
            new IrUnionCase("Circle", [new IrField("radius", ZType.Int)]),
            new IrUnionCase("Square", [new IrField("side", ZType.Int)])
        ]) { Type = ZType.Unit };

        var func = new IrNode.FuncDef("MakeCircle",
            [],
            shapeType,
            new IrNode.UnionCaseNew("Shape", "Circle",
                [new IrNode.IntConst(5) { Type = ZType.Int }])
            { Type = shapeType },
            false)
        { Type = new ZType.ZFuncType([], shapeType) };

        var seq = new IrNode.Seq([unionDecl, func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitListNew()
    {
        var listIntType = new ZType.ZNamedType("List", [ZType.Int]);

        var func = new IrNode.FuncDef("MakeList",
            [],
            listIntType,
            new IrNode.ListNew([
                new IrNode.IntConst(1) { Type = ZType.Int },
                new IrNode.IntConst(2) { Type = ZType.Int },
                new IrNode.IntConst(3) { Type = ZType.Int }
            ]) { Type = listIntType },
            false)
        { Type = new ZType.ZFuncType([], listIntType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitVectorNew()
    {
        var vecIntType = new ZType.ZNamedType("Vector", [ZType.Int]);

        var func = new IrNode.FuncDef("MakeVector",
            [],
            vecIntType,
            new IrNode.VectorNew([
                new IrNode.IntConst(1) { Type = ZType.Int },
                new IrNode.IntConst(2) { Type = ZType.Int }
            ]) { Type = vecIntType },
            false)
        { Type = new ZType.ZFuncType([], vecIntType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMapNew()
    {
        var mapType = new ZType.ZNamedType("Map", [ZType.String, ZType.Int]);

        var func = new IrNode.FuncDef("MakeMap",
            [],
            mapType,
            new IrNode.MapNew([
                (new IrNode.StringConst("a") { Type = ZType.String },
                 new IrNode.IntConst(1) { Type = ZType.Int })
            ]) { Type = mapType },
            false)
        { Type = new ZType.ZFuncType([], mapType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitThrow()
    {
        // (raise (new System.InvalidOperationException "boom"))
        var throwNode = new IrNode.Throw(
            new IrNode.ClrNew("System.InvalidOperationException",
                [new IrNode.StringConst("boom") { Type = ZType.String }])
            { Type = new ZType.ZNamedType("InvalidOperationException", []) })
        { Type = ZType.Int };

        var func = new IrNode.FuncDef("Fail",
            [],
            ZType.Int,
            throwNode,
            false)
        { Type = new ZType.ZFuncType([], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitPropagate()
    {
        // (? r) where r : Result<Int, ErrorInfo> — unwraps Ok or early-returns Err
        var propagateNode = new IrNode.Propagate(
            new IrNode.Var("r") { Type = ResultIntErrorInfo },
            ResultIntErrorInfo)
        { Type = ZType.Int };

        // Wrap the propagated value back in Ok to return Result<Int, ErrorInfo>
        var okWrapped = new IrNode.UnionCaseNew("Result", "Ok",
            [propagateNode])
        { Type = ResultIntErrorInfo };

        var func = new IrNode.FuncDef("PropagateResult",
            [new IrParam("r", ResultIntErrorInfo)],
            ResultIntErrorInfo, okWrapped, false)
        { Type = new ZType.ZFuncType([ResultIntErrorInfo], ResultIntErrorInfo) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, importedModules: StdlibModules);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClrNew()
    {
        // (new System.Object)
        var func = new IrNode.FuncDef("MakeObject",
            [],
            new ZType.ZNamedType("Object", []),
            new IrNode.ClrNew("System.Object", [])
            { Type = new ZType.ZNamedType("Object", []) },
            false)
        { Type = new ZType.ZFuncType([], new ZType.ZNamedType("Object", [])) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMethodCallProperty()
    {
        // (.Length s) where s : String
        var func = new IrNode.FuncDef("Len",
            [new IrParam("s", ZType.String)],
            ZType.Int,
            new IrNode.MethodCall(
                new IrNode.Var("s") { Type = ZType.String },
                "Length",
                [],
                IsProperty: true,
                IsIndexer: false)
            { Type = ZType.Int },
            false)
        { Type = new ZType.ZFuncType([ZType.String], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitLambdaNoCaptures()
    {
        // (fn (x) (+ x 1)) as a value inside a function
        var lambdaType = new ZType.ZFuncType([ZType.Int], ZType.Int);

        var innerLambda = new IrNode.FuncDef("__lambda0",
            [new IrParam("x", ZType.Int)],
            ZType.Int,
            new IrNode.BinOp("+",
                new IrNode.Var("x") { Type = ZType.Int },
                new IrNode.IntConst(1) { Type = ZType.Int })
            { Type = ZType.Int },
            false)
        { Type = lambdaType };

        var func = new IrNode.FuncDef("MakeIncrementer",
            [],
            lambdaType,
            innerLambda,
            false)
        { Type = new ZType.ZFuncType([], lambdaType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitClassDecl()
    {
        // Class fields are accessed via Var("fieldName") which resolves through _currentClassFields
        var classDecl = new IrNode.ClassDecl("Counter",
            [],
            [],
            [new IrField("count", ZType.Int)],
            [new IrObjectMethod("GetCount", [], ZType.Int,
                new IrNode.Var("count") { Type = ZType.Int })])
        { Type = ZType.Unit };

        var seq = new IrNode.Seq([classDecl]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitGenericMethodWithClrGenericCall()
    {
        // Generic function that calls a CLR generic method:
        // identity<T0>(x: T0) -> T0  { return x; }  -- simplest generic func
        // Then test calling it with int
        var typeVarId = 99;
        var typeVar = new ZType.ZTypeVar(typeVarId);
        var funcType = new ZType.ZFuncType([typeVar], typeVar);

        var body = new IrNode.Var("x") { Type = typeVar };

        var func = new IrNode.FuncDef("identity",
            [new IrParam("x", typeVar)],
            typeVar, body, false, TypeParams: ["T0"])
        { Type = funcType };

        // Call identity<int>(42) from main
        var call = new IrNode.Call(
            new IrNode.Var("identity") { Type = new ZType.ZFuncType([ZType.Int], ZType.Int) },
            [new IrNode.IntConst(42) { Type = ZType.Int }])
        { Type = ZType.Int };

        var main = new IrNode.FuncDef("main",
            [new IrParam("args", new ZType.ZNamedType("List", [ZType.String]))],
            ZType.Int, call, false)
        { Type = new ZType.ZFuncType([new ZType.ZNamedType("List", [ZType.String])], ZType.Int) };

        var seq = new IrNode.Seq([func, main]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);
        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var tempPath = Path.Combine(Path.GetTempPath(), $"test_generic_identity_{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            var loadCtx = new System.Runtime.Loader.AssemblyLoadContext("TestIdentity", isCollectible: true);
            try
            {
                var asm = loadCtx.LoadFromAssemblyPath(tempPath);
                var programType = asm.GetType("TestAssembly.Program")!;
                var identityMethod = programType.GetMethod("identity")!;
                var closed = identityMethod.MakeGenericMethod(typeof(int));
                var result = closed.Invoke(null, [42]);
                Assert.Equal(42, result);
            }
            finally { loadCtx.Unload(); }
        }
        finally { try { File.Delete(tempPath); } catch { } }
    }

    [Fact]
    public void EmitGenericMethodCallingClrGenericMethod()
    {
        // Generic function that calls a CLR generic static method inside its body:
        // wrap<T0>(x: T0) -> T0  { EqualityComparer<T0>.Default; return x; }
        // This tests emitting a CLR generic method call inside a generic function body.
        var typeVarId = 99;
        var typeVar = new ZType.ZTypeVar(typeVarId);
        var funcType = new ZType.ZFuncType([typeVar], typeVar);

        // Body: call System.Collections.Generic.EqualityComparer<T0>.get_Default(), pop, return x
        // Actually simpler: call Activator.CreateInstance<T0>() then pop and return x
        // Even simpler: call a generic static CLR method
        // Let's use IrNode.ClrCall to call a known generic method
        var clrCall = new IrNode.ClrCall(
            "System.Tuple", "Create",
            [new IrNode.Var("x") { Type = typeVar }],
            GenericArity: 1)
        { Type = new ZType.ZNamedType("Object", []) };

        // Just call Tuple.Create<T>(x) and return the result (ignore type mismatch for now)
        // Actually, just make the function call Tuple.Create and return x
        // Use Let to pop the result
        var body = new IrNode.Let("_tmp", clrCall,
            new IrNode.Var("x") { Type = typeVar })
        { Type = typeVar };

        var func = new IrNode.FuncDef("wrap",
            [new IrParam("x", typeVar)],
            typeVar, body, false, TypeParams: ["T0"])
        { Type = funcType };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);
        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var tempPath = Path.Combine(Path.GetTempPath(), $"test_generic_clr_{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            var loadCtx = new System.Runtime.Loader.AssemblyLoadContext("TestClrGeneric", isCollectible: true);
            try
            {
                var asm = loadCtx.LoadFromAssemblyPath(tempPath);
                var programType = asm.GetType("TestAssembly.Program")!;
                var wrapMethod = programType.GetMethod("wrap")!;
                Assert.True(wrapMethod.IsGenericMethodDefinition);
                var closed = wrapMethod.MakeGenericMethod(typeof(int));
                var result = closed.Invoke(null, [42]);
                Assert.Equal(42, result);
            }
            finally { loadCtx.Unload(); }
        }
        finally { try { File.Delete(tempPath); } catch { } }
    }

    [Fact]
    public void EmitGenericMethodMatchExtractOnUnion()
    {
        // unwrap<T0>(opt: Option[T0]) -> T0
        // Body: match opt { Some(v) -> v, None -> throw }
        var typeVarId = 50;
        var typeVar = new ZType.ZTypeVar(typeVarId);
        var optionType = new ZType.ZNamedType("Option", [typeVar]);
        var funcType = new ZType.ZFuncType([optionType], typeVar);

        var matchExpr = new IrNode.Match(
            new IrNode.Var("opt") { Type = optionType },
            [
                new IrMatchArm(
                    new IrPattern.Constructor("Some", [new IrPattern.Variable("v")]),
                    new IrNode.Var("v") { Type = typeVar }),
                new IrMatchArm(
                    new IrPattern.Constructor("None", []),
                    new IrNode.Throw(new IrNode.StringConst("oops") { Type = ZType.String }) { Type = typeVar })
            ])
        { Type = typeVar };

        var func = new IrNode.FuncDef("unwrap",
            [new IrParam("opt", optionType)],
            typeVar, matchExpr, false, TypeParams: ["T0"])
        { Type = funcType };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag, importedModules: StdlibModules);
        var bytes = emitter.Emit(seq);
        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var tempPath = Path.Combine(Path.GetTempPath(), $"test_generic_unwrap_{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            var loadCtx = new System.Runtime.Loader.AssemblyLoadContext("TestUnwrap", isCollectible: true);
            try
            {
                var asm = loadCtx.LoadFromAssemblyPath(tempPath);
                var programType = asm.GetType("TestAssembly.Program")!;
                var unwrapMethod = programType.GetMethod("unwrap")!;
                Assert.True(unwrapMethod.IsGenericMethodDefinition);

                var someType = asm.GetTypes().First(t => t.Name == "Some").MakeGenericType(typeof(int));
                var someInstance = Activator.CreateInstance(someType, 42);
                var closed = unwrapMethod.MakeGenericMethod(typeof(int));
                var result = closed.Invoke(null, [someInstance]);
                Assert.Equal(42, result);
            }
            finally { loadCtx.Unload(); }
        }
        finally { try { File.Delete(tempPath); } catch { } }
    }

    [Fact]
    public void EmitGenericMethodWithListCount()
    {
        // Generic function: list_count<T0>(xs: List[T0]) -> Int
        // Body: xs.Count (instance property)
        var typeVarId = 42;
        var listOfVar = new ZType.ZNamedType("List", [new ZType.ZTypeVar(typeVarId)]);
        var funcType = new ZType.ZFuncType([listOfVar], ZType.Int);

        var body = new IrNode.MethodCall(
            new IrNode.Var("xs") { Type = listOfVar },
            "Count", [], IsProperty: true, IsIndexer: false)
        { Type = ZType.Int };

        var func = new IrNode.FuncDef("list_count",
            [new IrParam("xs", listOfVar)],
            ZType.Int, body, false, TypeParams: ["T0"])
        { Type = funcType };

        // Caller: main function that creates list and calls list_count
        var listOfInt = new ZType.ZNamedType("List", [ZType.Int]);
        var listNew = new IrNode.ListNew([
            new IrNode.IntConst(1) { Type = ZType.Int },
            new IrNode.IntConst(2) { Type = ZType.Int }
        ]) { Type = listOfInt };

        var call = new IrNode.Call(
            new IrNode.Var("list_count") { Type = new ZType.ZFuncType([listOfInt], ZType.Int) },
            [listNew])
        { Type = ZType.Int };

        var main = new IrNode.FuncDef("main",
            [new IrParam("args", new ZType.ZNamedType("List", [ZType.String]))],
            ZType.Int, call, false)
        { Type = new ZType.ZFuncType([new ZType.ZNamedType("List", [ZType.String])], ZType.Int) };

        var seq = new IrNode.Seq([func, main]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestGenericAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // Actually load and run to verify IL validity
        var tempPath = Path.Combine(Path.GetTempPath(), $"test_generic_{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            var loadCtx = new System.Runtime.Loader.AssemblyLoadContext("TestGenericMethod", isCollectible: true);
            try
            {
                var asm = loadCtx.LoadFromAssemblyPath(tempPath);
                var programType = asm.GetType("TestGenericAssembly.Program");
                Assert.NotNull(programType);
                var mainMethod = programType!.GetMethod("list_count");
                Assert.NotNull(mainMethod);
                Assert.True(mainMethod!.IsGenericMethodDefinition, "list_count should be generic");

                // Call list_count<int>(ImmutableList<int>{1,2})
                var closed = mainMethod.MakeGenericMethod(typeof(int));
                var list = System.Collections.Immutable.ImmutableList.Create(1, 2);
                var result = closed.Invoke(null, [list]);
                Assert.Equal(2, result);
            }
            finally
            {
                loadCtx.Unload();
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }
}
