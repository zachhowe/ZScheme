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
    public void InferNullLiteral()
    {
        var type = InferExpr("null");
        Assert.IsType<ZType.ZTypeVar>(type);
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
    public void InferLetWithTypeAnnotation()
    {
        Assert.Equal(ZType.Int, InferExpr("(let [x : Int 5] (+ x 1))"));
    }

    [Fact]
    public void InferLetAnnotationNullable()
    {
        var type = InferExpr("(let [x : Int? 5] x)");
        Assert.IsType<ZType.ZNullableType>(type);
    }

    [Fact]
    public void InferLetAnnotationBoxing()
    {
        var type = InferExpr("(let [x : System.Object 5] x)");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("System.Object", named.Name);
    }

    [Fact]
    public void InferLetAnnotationBoxing_ShortObjectAlias()
    {
        // "Object" (without System prefix) should also allow boxing
        var (_, _, diag) = InferProgram("(let [x : Object 5] x)");
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void InferLetAnnotationMismatch_ReportsError()
    {
        var (_, _, diag) = InferProgram("(let [x : String 5] x)");
        Assert.True(diag.HasErrors);
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
    public void Await_FullyQualifiedTaskType()
    {
        // System.Threading.Tasks.Task should be recognized as Task by await and define-async
        var source = @"
(define-async (compute [x : Int]) : System.Threading.Tasks.Task (+ x 1))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
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

    [Fact]
    public void WithHandlers_ShadowedBySupertype_ReportsError()
    {
        // System.Exception catches everything, so DivideByZeroException is unreachable.
        // Matches CS0160 in the C# backend; also keeps IL semantics (dead-code handler)
        // from silently diverging from C#.
        var source = @"
(define (f [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.Exception _] 0)
    ([System.DivideByZeroException _] 1)
    (/ a b)))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d =>
            d.Message.Contains("unreachable") && d.Message.Contains("System.DivideByZeroException"));
    }

    [Fact]
    public void WithHandlers_DuplicateHandlerType_ReportsError()
    {
        var source = @"
(define (f [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.DivideByZeroException _] 0)
    ([System.DivideByZeroException _] 1)
    (/ a b)))";
        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("unreachable"));
    }

    [Fact]
    public void WithHandlers_SpecificBeforeGeneral_Allowed()
    {
        // Most-specific-first is the documented ordering; no diagnostic should fire.
        var source = @"
(define (f [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.DivideByZeroException _] 0)
    ([System.Exception _] 1)
    (/ a b)))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void WithHandlers_UnrelatedHandlers_Allowed()
    {
        // DivideByZeroException and ArgumentException are siblings under Exception
        // but not each other's supertype — either order is legal.
        var source = @"
(define (f [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.ArgumentException _] 0)
    ([System.DivideByZeroException _] 1)
    (/ a b)))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void InferTupleNew()
    {
        var type = InferExpr("(values 1 \"hello\")");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("ValueTuple", named.Name);
        Assert.Equal(2, named.TypeArgs.Count);
        Assert.Equal(ZType.Int, named.TypeArgs[0]);
        Assert.Equal(ZType.String, named.TypeArgs[1]);
    }

    [Fact]
    public void InferTupleThreeElements()
    {
        var type = InferExpr("(values 1 \"hello\" #t)");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("ValueTuple", named.Name);
        Assert.Equal(3, named.TypeArgs.Count);
        Assert.Equal(ZType.Bool, named.TypeArgs[2]);
    }

    [Fact]
    public void InferTupleAccessor()
    {
        var source = @"(define (f [t : (Int * String)]) : Int (value/0 t))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void InferTupleAccessorSecond()
    {
        var source = @"(define (f [t : (Int * String)]) : String (value/1 t))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void InferTuplePatternMatch()
    {
        var source = @"(define (swap [t : (Int * String)]) : (String * Int)
  (match t
    [(values x y) (values y x)]))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void InferTupleTypeAnnotation()
    {
        var source = @"(define (make-pair [x : Int] [y : String]) : (Int * String) (values x y))";
        var (_, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    [Fact]
    public void InferTupleToString()
    {
        var type = new ZType.ZNamedType("ValueTuple", [ZType.Int, ZType.String]);
        Assert.Equal("(Int * String)", type.ToString());
    }

    [Fact]
    public void InferNestedTuple()
    {
        var type = InferExpr("(values (values 1 2) (values 3 4))");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("ValueTuple", named.Name);
        Assert.Equal(2, named.TypeArgs.Count);
        var inner1 = Assert.IsType<ZType.ZNamedType>(named.TypeArgs[0]);
        Assert.Equal("ValueTuple", inner1.Name);
    }

    private static ZType InferLastForm(string source)
    {
        var (program, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return program.TopLevelForms[^1].ResolvedType!;
    }

    [Fact]
    public void With_OnRecord_ReturnsSameRecordType()
    {
        var type = InferLastForm(@"
(record Point [x : Int] [y : Int])
(define p (Point 1 2))
(with p [x 10])");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("Point", named.Name);
    }

    [Fact]
    public void With_MultipleUpdates_Types()
    {
        var type = InferLastForm(@"
(record Point [x : Int] [y : Int])
(define p (Point 1 2))
(with p [x 10] [y 20])");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("Point", named.Name);
    }

    [Fact]
    public void With_UnknownField_Errors()
    {
        var (_, _, diag) = InferProgram(@"
(record Point [x : Int] [y : Int])
(define p (Point 1 2))
(with p [nope 10])");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("has no field 'nope'"));
    }

    [Fact]
    public void With_FieldTypeMismatch_Errors()
    {
        var (_, _, diag) = InferProgram(@"
(record Point [x : Int] [y : Int])
(define p (Point 1 2))
(with p [x ""hello""])");
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void With_NonRecordTarget_Errors()
    {
        var (_, _, diag) = InferProgram("(with 42 [x 10])");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("'with' target must be a record"));
    }

    [Fact]
    public void With_GenericRecord_Types()
    {
        // Demonstrates inference works for a generic record with substituted type args.
        var type = InferLastForm(@"
(record (Box a) [value : a])
(define b (Box 42))
(with b [value 99])");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("Box", named.Name);
    }

    // --- struct ---

    [Fact]
    public void StructDecl_RegistersConstructorAndAccessors()
    {
        var (_, env, diag) = InferProgram("(struct Point [x : Int] [y : Int])");
        Assert.False(diag.HasErrors);
        Assert.NotNull(env.Lookup("Point"));
        Assert.NotNull(env.Lookup("Point/x"));
        Assert.NotNull(env.Lookup("Point/y"));
    }

    [Fact]
    public void StructDecl_ConstructorReturnsStructType()
    {
        var type = InferLastForm(@"
(struct Point [x : Int] [y : Int])
(Point 1 2)");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("Point", named.Name);
    }

    [Fact]
    public void With_OnStruct_ReturnsSameStructType()
    {
        var type = InferLastForm(@"
(struct Point [x : Int] [y : Int])
(define p (Point 1 2))
(with p [x 10])");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("Point", named.Name);
    }

    [Fact]
    public void StructDecl_Generic_Types()
    {
        var type = InferLastForm(@"
(struct (Box a) [value : a])
(Box 42)");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("Box", named.Name);
        Assert.Single(named.TypeArgs);
    }

    // --- (new ...) on user-defined types ---

    [Fact]
    public void ClrNew_OnUserRecord_TypesAsRecord()
    {
        // Phase-ordering fix: CLR reflection cannot see types from the current compilation,
        // so `(new UserRecord ...)` must resolve via the type environment first.
        var type = InferLastForm(@"
(record Point [x : Int] [y : Int])
(new Point 3 4)");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("Point", named.Name);
    }

    [Fact]
    public void ClrNew_OnUserStruct_TypesAsStruct()
    {
        var type = InferLastForm(@"
(struct Point [x : Int] [y : Int])
(new Point 3 4)");
        var named = Assert.IsType<ZType.ZNamedType>(type);
        Assert.Equal("Point", named.Name);
    }

    [Fact]
    public void ClrNew_UnknownType_StillReportsCLRError()
    {
        var (_, _, diag) = InferProgram("(new Totally.Bogus.Name)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("CLR type not found"));
    }

    // ─── Class method sibling calls ──────────────────────────────────

    [Fact]
    public void ClassMethod_CallsSibling_InfersCorrectly()
    {
        var source = @"
(class MathHelper
  (define (Double [x : Int]) : Int (+ x x))
  (define (Quadruple [x : Int]) : Int (Double (Double x))))";
        var (program, env, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var doubleType = env.Lookup("MathHelper/Double");
        Assert.NotNull(doubleType);
        var quadType = env.Lookup("MathHelper/Quadruple");
        Assert.NotNull(quadType);
    }

    [Fact]
    public void ClassMethod_CallsSelf_InfersCorrectly()
    {
        var source = @"
(class Counter
  (define (Countdown [n : Int]) : Int
    (if (= n 0) 0 (Countdown (- n 1)))))";
        var (program, env, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var countdownType = env.Lookup("Counter/Countdown");
        Assert.NotNull(countdownType);
    }

    [Fact]
    public void AsyncClassMethod_CallsSibling_InfersCorrectly()
    {
        var source = @"
(class Worker
  (define (Helper [x : Int]) : Int (+ x 1))
  (define-async (DoWork [x : Int]) : (Task Int) (Helper x)))";
        var (program, env, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var helperType = env.Lookup("Worker/Helper");
        Assert.NotNull(helperType);
        var doWorkType = env.Lookup("Worker/DoWork");
        Assert.NotNull(doWorkType);
        var ft = Assert.IsType<ZType.ZFuncType>(doWorkType);
        var retType = Assert.IsType<ZType.ZNamedType>(ft.Return);
        Assert.Equal("Task", retType.Name);
    }
}
