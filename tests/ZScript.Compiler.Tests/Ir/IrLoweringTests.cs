namespace ZScript.Compiler.Tests.Ir;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Types;
using Xunit;

public class IrLoweringTests
{
    private static IrLowering CreateLowering() => new(new DiagnosticBag());

    [Fact]
    public void IntLiteral_LowersToIntConst()
    {
        var lowering = CreateLowering();
        var result = lowering.Lower(new AstNode.IntLit(42, SourceSpan.None));
        var ic = Assert.IsType<IrNode.IntConst>(result);
        Assert.Equal(42, ic.Value);
    }

    [Fact]
    public void FloatLiteral_LowersToFloatConst()
    {
        var lowering = CreateLowering();
        var result = lowering.Lower(new AstNode.FloatLit(3.14f, SourceSpan.None));
        var fc = Assert.IsType<IrNode.FloatConst>(result);
        Assert.Equal(3.14f, fc.Value);
    }

    [Fact]
    public void BoolLiteral_LowersToBoolConst()
    {
        var lowering = CreateLowering();
        var result = lowering.Lower(new AstNode.BoolLit(true, SourceSpan.None));
        var bc = Assert.IsType<IrNode.BoolConst>(result);
        Assert.True(bc.Value);
    }

    [Fact]
    public void StringLiteral_LowersToStringConst()
    {
        var lowering = CreateLowering();
        var result = lowering.Lower(new AstNode.StringLit("hello", SourceSpan.None));
        var sc = Assert.IsType<IrNode.StringConst>(result);
        Assert.Equal("hello", sc.Value);
    }

    [Fact]
    public void UnitLiteral_LowersToUnitConst()
    {
        var lowering = CreateLowering();
        var result = lowering.Lower(new AstNode.UnitLit(SourceSpan.None));
        Assert.IsType<IrNode.UnitConst>(result);
    }

    [Fact]
    public void BinaryOp_LowersToBinOp()
    {
        var lowering = CreateLowering();
        var apply = new AstNode.Apply(
            new AstNode.Name("+", SourceSpan.None),
            [new AstNode.IntLit(1, SourceSpan.None), new AstNode.IntLit(2, SourceSpan.None)],
            SourceSpan.None);

        var result = lowering.Lower(apply);

        var binop = Assert.IsType<IrNode.BinOp>(result);
        Assert.Equal("+", binop.Op);
        Assert.IsType<IrNode.IntConst>(binop.Left);
        Assert.IsType<IrNode.IntConst>(binop.Right);
    }

    [Fact]
    public void UnaryOp_LowersToUnaryOp()
    {
        var lowering = CreateLowering();
        var apply = new AstNode.Apply(
            new AstNode.Name("not", SourceSpan.None),
            [new AstNode.BoolLit(true, SourceSpan.None)],
            SourceSpan.None);

        var result = lowering.Lower(apply);

        var unary = Assert.IsType<IrNode.UnaryOp>(result);
        Assert.Equal("not", unary.Op);
    }

    [Fact]
    public void LetBinding_LowersToIrLet()
    {
        var lowering = CreateLowering();
        var let = new AstNode.Let("x",
            new AstNode.IntLit(5, SourceSpan.None),
            new AstNode.Name("x", SourceSpan.None),
            SourceSpan.None);

        var result = lowering.Lower(let);

        var irLet = Assert.IsType<IrNode.Let>(result);
        Assert.Equal("x", irLet.VarName);
        Assert.IsType<IrNode.IntConst>(irLet.Value);
    }

    [Fact]
    public void IfExpression_LowersToIrIf()
    {
        var lowering = CreateLowering();
        var ifExpr = new AstNode.If(
            new AstNode.BoolLit(true, SourceSpan.None),
            new AstNode.IntLit(1, SourceSpan.None),
            new AstNode.IntLit(0, SourceSpan.None),
            SourceSpan.None);

        var result = lowering.Lower(ifExpr);

        var irIf = Assert.IsType<IrNode.If>(result);
        Assert.IsType<IrNode.BoolConst>(irIf.Condition);
        Assert.IsType<IrNode.IntConst>(irIf.Then);
        Assert.IsType<IrNode.IntConst>(irIf.Else);
    }

    [Fact]
    public void Define_LowersToFuncDef()
    {
        var lowering = CreateLowering();
        var define = new AstNode.Define("id",
            [new Param("x", null, SourceSpan.None)],
            null,
            new AstNode.Name("x", SourceSpan.None),
            SourceSpan.None);

        var result = lowering.Lower(define);

        var funcDef = Assert.IsType<IrNode.FuncDef>(result);
        Assert.Equal("id", funcDef.Name);
        Assert.Single(funcDef.Params);
        Assert.Equal("x", funcDef.Params[0].Name);
    }

    [Fact]
    public void DefineValue_LowersToLet()
    {
        var lowering = CreateLowering();
        var defVal = new AstNode.DefineValue("pi",
            new AstNode.FloatLit(3.14f, SourceSpan.None),
            SourceSpan.None);

        var result = lowering.Lower(defVal);

        var irLet = Assert.IsType<IrNode.Let>(result);
        Assert.Equal("pi", irLet.VarName);
    }

    [Fact]
    public void CollectionMethod_LowersToMethodCall()
    {
        var lowering = CreateLowering();
        var apply = new AstNode.Apply(
            new AstNode.Name("list/head", SourceSpan.None),
            [new AstNode.Name("xs", SourceSpan.None)],
            SourceSpan.None);

        var result = lowering.Lower(apply);

        var mc = Assert.IsType<IrNode.MethodCall>(result);
        Assert.Equal("Head", mc.MethodName);
        Assert.True(mc.IsProperty);
    }

    [Fact]
    public void BuiltinCtor_LowersToBuiltinCtorCall()
    {
        var lowering = CreateLowering();
        var apply = new AstNode.Apply(
            new AstNode.Name("Ok", SourceSpan.None),
            [new AstNode.IntLit(42, SourceSpan.None)],
            SourceSpan.None);

        var result = lowering.Lower(apply);

        var ctor = Assert.IsType<IrNode.BuiltinCtorCall>(result);
        Assert.Equal("ZsResult", ctor.RuntimeTypeName);
        Assert.Equal("Ok", ctor.CaseName);
    }

    [Fact]
    public void ImportClr_RegistersImport()
    {
        var lowering = CreateLowering();
        var importClr = new AstNode.ImportClr(
            [new ClrImport("sqrt", "System.Math/Sqrt", SourceSpan.None)],
            SourceSpan.None);

        lowering.Lower(importClr);

        Assert.True(lowering.ClrImports.ContainsKey("sqrt"));
        Assert.Equal("System.Math", lowering.ClrImports["sqrt"].TypeName);
        Assert.Equal("Sqrt", lowering.ClrImports["sqrt"].MethodName);
    }
}
