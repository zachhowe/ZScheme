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
    private static readonly ZType ResultIntError = new ZType.ZNamedType("Result", [ZType.Int, new ZType.ZNamedType("Error", [])]);
    private static readonly ZType OptionInt = new ZType.ZNamedType("Option", [ZType.Int]);
    private static readonly ZType ErrorType = new ZType.ZNamedType("Error", []);

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
    public void EmitBuiltinCtorCallOk()
    {
        // Ok(42) : Result<Int, Error>
        var ctorCall = new IrNode.BuiltinCtorCall(
            "ZsResult", "Ok",
            [new IrNode.IntConst(42) { Type = ZType.Int }],
            [ZType.Int, ErrorType])
        { Type = ResultIntError };

        var func = new IrNode.FuncDef("MakeOk", [], ResultIntError, ctorCall, false)
        { Type = new ZType.ZFuncType([], ResultIntError) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitBuiltinCtorCallErr()
    {
        // Err(Error("bad")) : Result<Int, Error>
        var errorCtor = new IrNode.BuiltinCtorCall(
            "ZsError", null,
            [new IrNode.StringConst("bad") { Type = ZType.String }],
            [])
        { Type = ErrorType };

        var errCtor = new IrNode.BuiltinCtorCall(
            "ZsResult", "Err",
            [errorCtor],
            [ZType.Int, ErrorType])
        { Type = ResultIntError };

        var func = new IrNode.FuncDef("MakeErr", [], ResultIntError, errCtor, false)
        { Type = new ZType.ZFuncType([], ResultIntError) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitBuiltinCtorCallSomeAndNone()
    {
        // Some(10) : Option<Int>
        var someCtor = new IrNode.BuiltinCtorCall(
            "ZsOption", "Some",
            [new IrNode.IntConst(10) { Type = ZType.Int }],
            [ZType.Int])
        { Type = OptionInt };

        var someFunc = new IrNode.FuncDef("MakeSome", [], OptionInt, someCtor, false)
        { Type = new ZType.ZFuncType([], OptionInt) };

        // None : Option<Int>
        var noneCtor = new IrNode.BuiltinCtorCall(
            "ZsOption", "None",
            [],
            [ZType.Int])
        { Type = OptionInt };

        var noneFunc = new IrNode.FuncDef("MakeNone", [], OptionInt, noneCtor, false)
        { Type = new ZType.ZFuncType([], OptionInt) };

        var seq = new IrNode.Seq([someFunc, noneFunc]) { Type = ZType.Unit };
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
        var emitter = new IlEmitter("TestAssembly", diag);
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
        // (catch (some-clr-call x)) -> Result<Int, Error>
        var clrCall = new IrNode.ClrCall(
            "System.Int32", "Parse",
            [new IrNode.Var("s") { Type = ZType.String }])
        { Type = ZType.Int };

        var tryCatch = new IrNode.TryCatch(clrCall)
        { Type = ResultIntError };

        var func = new IrNode.FuncDef("SafeParse", [new IrParam("s", ZType.String)],
            ResultIntError, tryCatch, false)
        { Type = new ZType.ZFuncType([ZType.String], ResultIntError) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitPropagate()
    {
        // Define inner: safe-div(a, b) -> Result<Int, Error>
        var safeDivFunc = new IrNode.FuncDef(
            "safe_div",
            [new IrParam("a", ZType.Int), new IrParam("b", ZType.Int)],
            ResultIntError,
            new IrNode.If(
                new IrNode.BinOp("=",
                    new IrNode.Var("b") { Type = ZType.Int },
                    new IrNode.IntConst(0) { Type = ZType.Int })
                { Type = ZType.Bool },
                new IrNode.BuiltinCtorCall("ZsResult", "Err",
                    [new IrNode.BuiltinCtorCall("ZsError", null,
                        [new IrNode.StringConst("div by zero") { Type = ZType.String }], [])
                    { Type = ErrorType }],
                    [ZType.Int, ErrorType])
                { Type = ResultIntError },
                new IrNode.BuiltinCtorCall("ZsResult", "Ok",
                    [new IrNode.BinOp("/",
                        new IrNode.Var("a") { Type = ZType.Int },
                        new IrNode.Var("b") { Type = ZType.Int })
                    { Type = ZType.Int }],
                    [ZType.Int, ErrorType])
                { Type = ResultIntError })
            { Type = ResultIntError },
            false)
        { Type = new ZType.ZFuncType([ZType.Int, ZType.Int], ResultIntError) };

        // Define outer: test(x) = let v = ?(safe_div(x, 2)) in Ok(v + 1)
        var propagateExpr = new IrNode.Propagate(
            new IrNode.Call(
                new IrNode.Var("safe_div") { Type = new ZType.ZFuncType([ZType.Int, ZType.Int], ResultIntError) },
                [new IrNode.Var("x") { Type = ZType.Int }, new IrNode.IntConst(2) { Type = ZType.Int }])
            { Type = ResultIntError },
            ResultIntError)
        { Type = ZType.Int };

        var body = new IrNode.Let("v", propagateExpr,
            new IrNode.BuiltinCtorCall("ZsResult", "Ok",
                [new IrNode.BinOp("+",
                    new IrNode.Var("v") { Type = ZType.Int },
                    new IrNode.IntConst(1) { Type = ZType.Int })
                { Type = ZType.Int }],
                [ZType.Int, ErrorType])
            { Type = ResultIntError })
        { Type = ResultIntError };

        var testFunc = new IrNode.FuncDef("test", [new IrParam("x", ZType.Int)],
            ResultIntError, body, false)
        { Type = new ZType.ZFuncType([ZType.Int], ResultIntError) };

        var seq = new IrNode.Seq([safeDivFunc, testFunc]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitBuiltinCtorCallError()
    {
        // (Error "something went wrong")
        var errorCtor = new IrNode.BuiltinCtorCall(
            "ZsError", null,
            [new IrNode.StringConst("something went wrong") { Type = ZType.String }],
            [])
        { Type = ErrorType };

        var func = new IrNode.FuncDef("MakeError", [], ErrorType, errorCtor, false)
        { Type = new ZType.ZFuncType([], ErrorType) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void EmitMatchOnResult()
    {
        // match r { Ok(v) => v, Err(e) => 0 }
        var matchNode = new IrNode.Match(
            new IrNode.Var("r") { Type = ResultIntError },
            [
                new IrMatchArm(
                    new IrPattern.Constructor("Ok", [new IrPattern.Variable("v")]),
                    new IrNode.Var("v") { Type = ZType.Int }),
                new IrMatchArm(
                    new IrPattern.Constructor("Err", [new IrPattern.Variable("e")]),
                    new IrNode.IntConst(0) { Type = ZType.Int })
            ])
        { Type = ZType.Int };

        var func = new IrNode.FuncDef("UnwrapResult", [new IrParam("r", ResultIntError)],
            ZType.Int, matchNode, false)
        { Type = new ZType.ZFuncType([ResultIntError], ZType.Int) };

        var seq = new IrNode.Seq([func]) { Type = ZType.Unit };
        var diag = new DiagnosticBag();
        var emitter = new IlEmitter("TestAssembly", diag);
        var bytes = emitter.Emit(seq);

        Assert.NotNull(bytes);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }
}
