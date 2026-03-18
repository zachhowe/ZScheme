namespace ZScript.Compiler.Tests.Types;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;
using Xunit;

public class TypeInfererTests
{
    private static (AstNode.Program program, TypeEnv env, DiagnosticBag diag) InferProgram(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);

        var env = TypeEnv.CreateRoot();
        var inferer = new TypeInferer(diag);
        inferer.Infer(program, env);
        inferer.Resolve(program);

        return (program, env, diag);
    }

    private static ZType InferExpr(string source)
    {
        var (program, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return program.TopLevelForms[0].ResolvedType!;
    }

    [Fact]
    public void InferIntLiteral()
    {
        Assert.Equal(ZType.Int, InferExpr("42"));
    }

    [Fact]
    public void InferFloatLiteral()
    {
        Assert.Equal(ZType.Float, InferExpr("3.14"));
    }

    [Fact]
    public void InferBoolLiteral()
    {
        Assert.Equal(ZType.Bool, InferExpr("true"));
    }

    [Fact]
    public void InferStringLiteral()
    {
        Assert.Equal(ZType.String, InferExpr("\"hello\""));
    }

    [Fact]
    public void InferAddition()
    {
        Assert.Equal(ZType.Int, InferExpr("(+ 1 2)"));
    }

    [Fact]
    public void InferComparison()
    {
        Assert.Equal(ZType.Bool, InferExpr("(= 1 2)"));
    }

    [Fact]
    public void InferIfExpression()
    {
        Assert.Equal(ZType.Int, InferExpr("(if true 1 2)"));
    }

    [Fact]
    public void InferIfBranchMismatch_ReportsError()
    {
        var (_, _, diag) = InferProgram("(if true 1 \"hello\")");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void InferLetBinding()
    {
        Assert.Equal(ZType.Int, InferExpr("(let [x 5] (+ x 1))"));
    }

    [Fact]
    public void InferLambda()
    {
        var type = InferExpr("(fn [x y] (+ x y))");
        var ft = Assert.IsType<ZType.ZFuncType>(type);
        Assert.Equal(2, ft.Params.Count);
        Assert.Equal(ZType.Int, ft.Params[0]);
        Assert.Equal(ZType.Int, ft.Params[1]);
        Assert.Equal(ZType.Int, ft.Return);
    }

    [Fact]
    public void InferDefineFunction()
    {
        var source = "(define (add [x : Int] [y : Int]) : Int (+ x y))";
        var (program, env, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var addType = env.Lookup("add");
        Assert.NotNull(addType);
        var ft = Assert.IsType<ZType.ZFuncType>(addType);
        Assert.Equal(2, ft.Params.Count);
        Assert.Equal(ZType.Int, ft.Return);
    }

    [Fact]
    public void InferRecursiveFunction()
    {
        var source = @"(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))";
        var (_, env, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var fType = env.Lookup("factorial");
        Assert.NotNull(fType);
    }

    [Fact]
    public void InferListExpr()
    {
        var type = InferExpr("(list 1 2 3)");
        var nt = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("List", nt.Name);
        Assert.Single(nt.TypeArgs);
        Assert.Equal(ZType.Int, nt.TypeArgs[0]);
    }

    [Fact]
    public void InferVectorExpr()
    {
        var type = InferExpr("(vector 1 2 3)");
        var nt = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("Vector", nt.Name);
    }

    [Fact]
    public void InferMapExpr()
    {
        var type = InferExpr("(map-of (\"a\" 1) (\"b\" 2))");
        var nt = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("Map", nt.Name);
        Assert.Equal(2, nt.TypeArgs.Count);
        Assert.Equal(ZType.String, nt.TypeArgs[0]);
        Assert.Equal(ZType.Int, nt.TypeArgs[1]);
    }

    [Fact]
    public void InferNestedLet()
    {
        var source = "(let [x 5] (let [y (+ x 1)] (+ x y)))";
        Assert.Equal(ZType.Int, InferExpr(source));
    }

    [Fact]
    public void UndefinedVariable_ReportsError()
    {
        var (_, _, diag) = InferProgram("(+ x 1)");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void InferDefineValue()
    {
        var source = "(define x 42)";
        var (_, env, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.NotNull(env.Lookup("x"));
    }

    [Fact]
    public void InferMultipleDefines()
    {
        var source = @"
(define (add [x : Int] [y : Int]) : Int (+ x y))
(define (double [x : Int]) : Int (add x x))";
        var (_, env, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.NotNull(env.Lookup("add"));
        Assert.NotNull(env.Lookup("double"));
    }
}
