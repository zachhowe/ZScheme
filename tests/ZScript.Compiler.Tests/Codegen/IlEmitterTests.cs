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
