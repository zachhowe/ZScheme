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
            [],
            SourceSpan.None);

        lowering.Lower(importClr);

        Assert.True(lowering.ClrImports.ContainsKey("sqrt"));
        Assert.Equal("System.Math", lowering.ClrImports["sqrt"].TypeName);
        Assert.Equal("Sqrt", lowering.ClrImports["sqrt"].MethodName);
    }

    [Fact]
    public void Lambda_UsesInferredParamTypes()
    {
        var lowering = CreateLowering();
        var lambda = new AstNode.Lambda(
            [new Param("x", null, SourceSpan.None)],
            new AstNode.Name("x", SourceSpan.None),
            SourceSpan.None)
        {
            ResolvedType = new ZType.ZFuncType([ZType.Int], ZType.Int)
        };

        var result = lowering.Lower(lambda);

        var funcDef = Assert.IsType<IrNode.FuncDef>(result);
        Assert.Single(funcDef.Params);
        Assert.Equal(ZType.Int, funcDef.Params[0].Type);
    }

    [Fact]
    public void RecordCtor_LowersToRecordNew()
    {
        var lowering = CreateLowering();
        // First register the record
        var recordDecl = new AstNode.RecordDecl("Point",
            [],
            [new FieldDecl("x", ZType.Int, SourceSpan.None), new FieldDecl("y", ZType.Int, SourceSpan.None)],
            SourceSpan.None);
        lowering.Lower(recordDecl);

        // Then lower a call using the record name
        var apply = new AstNode.Apply(
            new AstNode.Name("Point", SourceSpan.None),
            [new AstNode.IntLit(1, SourceSpan.None), new AstNode.IntLit(2, SourceSpan.None)],
            SourceSpan.None);

        var result = lowering.Lower(apply);

        var recNew = Assert.IsType<IrNode.RecordNew>(result);
        Assert.Equal("Point", recNew.TypeName);
        Assert.Equal(2, recNew.Fields.Count);
        Assert.Equal("x", recNew.Fields[0].FieldName);
        Assert.Equal("y", recNew.Fields[1].FieldName);
    }

    [Fact]
    public void StringAppend_LowersToBinOp()
    {
        var lowering = CreateLowering();
        var apply = new AstNode.Apply(
            new AstNode.Name("string-append", SourceSpan.None),
            [new AstNode.StringLit("hello", SourceSpan.None), new AstNode.StringLit(" world", SourceSpan.None)],
            SourceSpan.None);

        var result = lowering.Lower(apply);

        var binOp = Assert.IsType<IrNode.BinOp>(result);
        Assert.Equal("+", binOp.Op);
    }

    [Fact]
    public void IntToString_LowersToMethodCall()
    {
        var lowering = CreateLowering();
        var apply = new AstNode.Apply(
            new AstNode.Name("int->string", SourceSpan.None),
            [new AstNode.IntLit(42, SourceSpan.None)],
            SourceSpan.None);

        var result = lowering.Lower(apply);

        var mc = Assert.IsType<IrNode.MethodCall>(result);
        Assert.Equal("ToString", mc.MethodName);
    }

    [Fact]
    public void ObjectExpr_LowersToIrObjectExpr()
    {
        var lowering = CreateLowering();
        var objExpr = new AstNode.ObjectExpr(
            ["IComparer"],
            [
                new ObjectMethod("Compare",
                    [new Param("x", ZType.Int, SourceSpan.None), new Param("y", ZType.Int, SourceSpan.None)],
                    ZType.Int,
                    new AstNode.Apply(
                        new AstNode.Name("-", SourceSpan.None),
                        [new AstNode.Name("x", SourceSpan.None), new AstNode.Name("y", SourceSpan.None)],
                        SourceSpan.None),
                    SourceSpan.None)
            ],
            SourceSpan.None);

        var result = lowering.Lower(objExpr);

        var irObj = Assert.IsType<IrNode.ObjectExpr>(result);
        Assert.Single(irObj.InterfaceNames);
        Assert.Equal("IComparer", irObj.InterfaceNames[0]);
        Assert.Single(irObj.Methods);
        Assert.Equal("Compare", irObj.Methods[0].Name);
        Assert.Equal(2, irObj.Methods[0].Params.Count);
        Assert.Equal(ZType.Int, irObj.Methods[0].ReturnType);
        Assert.IsType<IrNode.BinOp>(irObj.Methods[0].Body);
    }

    [Fact]
    public void DefineAsync_SetsIsAsyncFlag()
    {
        var lowering = CreateLowering();
        var body = new AstNode.IntLit(42, SourceSpan.None) { ResolvedType = ZType.Int };
        var defAsync = new AstNode.DefineAsync(
            "compute",
            [new Param("x", ZType.Int, SourceSpan.None)],
            new ZType.ZNamedType("Task", [ZType.Int]),
            body,
            SourceSpan.None)
        { ResolvedType = new ZType.ZFuncType([ZType.Int], new ZType.ZNamedType("Task", [ZType.Int])) };

        var result = lowering.Lower(defAsync);

        var funcDef = Assert.IsType<IrNode.FuncDef>(result);
        Assert.True(funcDef.IsAsync);
        Assert.Equal("compute", funcDef.Name);
        Assert.Equal(ZType.Int, funcDef.ReturnType); // Unwrapped from Task<Int>
    }

    [Fact]
    public void DefineAsync_NonGenericTask_ReturnTypeIsUnit()
    {
        var lowering = CreateLowering();
        var body = new AstNode.IntLit(0, SourceSpan.None) { ResolvedType = ZType.Int };
        var defAsync = new AstNode.DefineAsync(
            "work",
            [],
            new ZType.ZNamedType("Task", []),
            body,
            SourceSpan.None)
        { ResolvedType = new ZType.ZFuncType([], new ZType.ZNamedType("Task", [])) };

        var result = lowering.Lower(defAsync);

        var funcDef = Assert.IsType<IrNode.FuncDef>(result);
        Assert.True(funcDef.IsAsync);
        Assert.Equal(ZType.Unit, funcDef.ReturnType);
    }

    [Fact]
    public void Await_LowersToIrAwait()
    {
        var lowering = CreateLowering();
        var inner = new AstNode.Name("x", SourceSpan.None)
            { ResolvedType = new ZType.ZNamedType("Task", [ZType.Int]) };
        var awaitNode = new AstNode.Await(inner, SourceSpan.None)
            { ResolvedType = ZType.Int };

        var result = lowering.Lower(awaitNode);

        var irAwait = Assert.IsType<IrNode.Await>(result);
        Assert.Equal(ZType.Int, irAwait.Type);
        Assert.IsType<IrNode.Var>(irAwait.Expr);
    }

    [Fact]
    public void DefineWithoutAsync_IsAsyncIsFalse()
    {
        var lowering = CreateLowering();
        var body = new AstNode.IntLit(1, SourceSpan.None) { ResolvedType = ZType.Int };
        var define = new AstNode.Define(
            "f",
            [new Param("x", ZType.Int, SourceSpan.None)],
            ZType.Int,
            body,
            SourceSpan.None)
        { ResolvedType = new ZType.ZFuncType([ZType.Int], ZType.Int) };

        var result = lowering.Lower(define);

        var funcDef = Assert.IsType<IrNode.FuncDef>(result);
        Assert.False(funcDef.IsAsync);
    }
}
