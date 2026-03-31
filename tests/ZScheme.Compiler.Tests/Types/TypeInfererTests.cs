using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

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
        Assert.Equal(ZType.Bool, InferExpr("#t"));
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
    public void InferFloatAddition()
    {
        Assert.Equal(ZType.Float, InferExpr("(+ 1.0 2.0)"));
    }

    [Fact]
    public void InferFloatMultiplication()
    {
        Assert.Equal(ZType.Float, InferExpr("(* 3.0 4.0)"));
    }

    [Fact]
    public void InferMixedArithmeticFails()
    {
        var (_, _, diag) = InferProgram("(+ 1 1.0)");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void InferStringAdditionFails()
    {
        var (_, _, diag) = InferProgram("(+ \"a\" \"b\")");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void InferFloatComparison()
    {
        Assert.Equal(ZType.Bool, InferExpr("(< 1.0 2.0)"));
    }

    [Fact]
    public void InferComparison()
    {
        Assert.Equal(ZType.Bool, InferExpr("(= 1 2)"));
    }

    [Fact]
    public void InferIfExpression()
    {
        Assert.Equal(ZType.Int, InferExpr("(if #t 1 2)"));
    }

    [Fact]
    public void InferIfBranchMismatch_ReportsError()
    {
        var (_, _, diag) = InferProgram("(if #t 1 \"hello\")");
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
        // With constrained polymorphism, params are constrained vars (Int|Float), not concrete Int
        Assert.IsType<ZType.ZConstrainedVar>(ft.Params[0]);
        Assert.IsType<ZType.ZConstrainedVar>(ft.Params[1]);
        Assert.IsType<ZType.ZConstrainedVar>(ft.Return);
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
    public void InferClrNew_Object()
    {
        var type = InferExpr("(new System.Object)");
        var nt = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("System.Object", nt.Name);
    }

    [Fact]
    public void InferClrNew_WithArg()
    {
        var type = InferExpr("(new System.Collections.ArrayList 10)");
        var nt = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("System.Collections.ArrayList", nt.Name);
    }

    [Fact]
    public void InferClrNew_UnknownType_Error()
    {
        var (_, _, diag) = InferProgram("(new Nonexistent.Fake.Type)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("CLR type not found"));
    }

    [Fact]
    public void InferClrNew_WrongArgCount_Error()
    {
        var (_, _, diag) = InferProgram("(new System.Object 1 2 3)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("constructor"));
    }

    [Fact]
    public void InferLetStar()
    {
        Assert.Equal(ZType.Int, InferExpr("(let* ([x 5] [y (+ x 1)]) y)"));
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

    [Fact]
    public void RaiseUnifiesWithAnyType()
    {
        // raise returns a fresh type var, so it unifies with Int in the other branch
        var source = @"
(define (f [x : Bool]) : Int
  (if x 42 (raise (new System.Exception ""fail""))))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void RaiseNonExceptionType_ReportsError()
    {
        var source = @"(raise (new System.Object))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void RaiseStringType_ReportsError()
    {
        var source = @"(raise ""hello"")";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void DefineAsync_InfersTaskReturnType()
    {
        var source = @"(define-async (compute [x : Int]) : (Task Int) (+ x 1))";
        var (program, env, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var funcType = env.Lookup("compute");
        Assert.NotNull(funcType);
        var ft = Assert.IsType<ZType.ZFuncType>(funcType);
        var retType = Assert.IsType<ZType.ZNamedType>(ft.Return);
        Assert.Equal("Task", retType.Name);
        Assert.Single(retType.TypeArgs);
        Assert.Equal(ZType.Int, retType.TypeArgs[0]);
    }

    [Fact]
    public void Await_UnwrapsTaskType()
    {
        var source = @"
(define-async (compute [x : Int]) : (Task Int) (+ x 1))
(define-async (use-it [x : Int]) : (Task Int) (await (compute x)))";
        var (program, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // The second define-async body contains an await, which should resolve to Int
        var defAsync = (AstNode.DefineAsync)program.TopLevelForms[1];
        var awaitNode = (AstNode.Await)defAsync.Body;
        Assert.Equal(ZType.Int, awaitNode.ResolvedType);
    }

    [Fact]
    public void Await_NonGenericTask_ReturnsUnit()
    {
        var source = @"
(define-async (wait) : Task 0)
(define-async (use-wait) : (Task Int)
  (let [_ (await (wait))]
    42))";
        var (program, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // The await of non-generic Task should yield Unit
        var defAsync = (AstNode.DefineAsync)program.TopLevelForms[1];
        var letNode = (AstNode.Let)defAsync.Body;
        var awaitNode = (AstNode.Await)letNode.Value;
        Assert.Equal(ZType.Unit, awaitNode.ResolvedType);
    }

    [Fact]
    public void Await_ErrorOnNonTaskType()
    {
        var source = @"(define-async (bad) : (Task Int) (await 42))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void Await_AtTopLevel_ReportsError()
    {
        var source = @"
(define-async (get-value) : (Task Int) 42)
(await (get-value))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors, "Expected error for await at top level");
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("'await' can only be used inside an async function"));
    }

    [Fact]
    public void Await_InsideRegularDefine_ReportsError()
    {
        var source = @"
(define-async (get-value) : (Task Int) 42)
(define (bad) : Int (await (get-value)))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors, "Expected error for await inside regular define");
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("'await' can only be used inside an async function"));
    }

    [Fact]
    public void Await_NestedInLetInsideDefineAsync_Succeeds()
    {
        var source = @"
(define-async (get-value) : (Task Int) 42)
(define-async (use-it) : (Task Int)
  (let [x (await (get-value))]
    (+ x 1)))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void Await_InsideLambdaInDefineAsync_ReportsError()
    {
        var source = @"
(define-async (get-value) : (Task Int) 42)
(define-async (bad) : (Task Int)
  (let [f (fn [] (await (get-value)))]
    42))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors, "Expected error for await inside lambda");
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("'await' can only be used inside an async function"));
    }

    [Fact]
    public void Await_InsideModuleBody_ReportsError()
    {
        var source = @"
(module Foo
  (define-async (get-value) : (Task Int) 42)
  (await (get-value)))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors, "Expected error for await in module body");
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("'await' can only be used inside an async function"));
    }

    [Fact]
    public void VariadicFunction_InfersType()
    {
        var source = "(define (fmt [s : String] [args : String ...]) : String s)";
        var (program, env, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var fmtType = env.Lookup("fmt");
        Assert.NotNull(fmtType);
        var ft = Assert.IsType<ZType.ZFuncType>(fmtType);
        Assert.Equal(2, ft.Params.Count);
        Assert.True(ft.IsVariadic);
        Assert.Equal(ZType.String, ft.Return);
    }

    [Fact]
    public void VariadicFunction_CallWithMultipleArgs()
    {
        var source = @"
(define (fmt [s : String] [args : String ...]) : String s)
(fmt ""hello"" ""a"" ""b"" ""c"")";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void VariadicFunction_CallWithNoVarargs()
    {
        var source = @"
(define (fmt [s : String] [args : String ...]) : String s)
(fmt ""hello"")";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void VariadicFunction_TooFewArgs_ReportsError()
    {
        var source = @"
(define (fmt [s : String] [x : Int] [args : String ...]) : String s)
(fmt)";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Too few arguments"));
    }

    // --- WithHandlers ---

    [Fact]
    public void WithHandlers_InfersBodyType()
    {
        var source = @"
(define (safe-div [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.DivideByZeroException _] 0)
    (/ a b)))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void WithHandlers_HandlerTypeMismatch_ReportsError()
    {
        var source = @"
(define (f [x : Int]) : Int
  (with-handlers
    ([System.Exception _] ""not an int"")
    x))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void WithHandlers_InvalidExceptionType_ReportsError()
    {
        var source = @"
(define (f [x : Int]) : Int
  (with-handlers
    ([No.Such.Type _] 0)
    x))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("not found"));
    }

    [Fact]
    public void WithHandlers_NonExceptionType_ReportsError()
    {
        var source = @"
(define (f [x : Int]) : Int
  (with-handlers
    ([System.Object _] 0)
    x))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("must be a System.Exception subclass"));
    }

    [Fact]
    public void WithHandlers_BindingVarAccessible()
    {
        // The handler body references 'e', which should be in scope as the exception binding
        var source = @"
(import-clr
  [ex-message System.Exception.Message :instance-property : (Fn [System.Exception] String)])

(define (f [x : Int]) : String
  (with-handlers
    ([System.Exception e] (ex-message e))
    (begin x ""ok"")))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }
}
