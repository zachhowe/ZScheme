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
        ("Option", [
            new IrNode.UnionDecl("Option", ["a"], [
                new IrUnionCase("Some", [new IrField("value", new ZType.ZNamedType("a", []))]),
                new IrUnionCase("None", [])
            ])
        ]),
        ("Result", [
            new IrNode.UnionDecl("Result", ["a", "e"], [
                new IrUnionCase("Ok", [new IrField("value", new ZType.ZNamedType("a", []))]),
                new IrUnionCase("Err", [new IrField("error", new ZType.ZNamedType("e", []))])
            ])
        ]),
        ("Error", [
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
}
