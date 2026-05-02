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
        // The Resolve pass defaults free numeric ZConstrainedVars to their preferred
        // concrete kind (Int) so that codegen never sees an unresolved numeric var,
        // which previously fell through to System.Object and produced unverifiable IL
        // (e.g. `sub` on object refs).
        Assert.Equal(ZType.Int, ft.Params[0]);
        Assert.Equal(ZType.Int, ft.Params[1]);
        Assert.Equal(ZType.Int, ft.Return);
    }

    [Fact]
    public void DefaultsFreeNumericConstrainedVar_FromUnusedUnionParam()
    {
        // Regression: extracting a value from a polymorphic union case whose
        // type parameter is otherwise unconstrained, then using it with a
        // numeric operator (`-`), used to leave the value's type as a free
        // ZConstrainedVar after Resolve. Codegen would then fall through to
        // System.Object. The Resolve pass now defaults free numeric vars to
        // Int, which is the expected and verifiable outcome.
        var source = @"(union (FUn ^a ^b) (Left [lv : ^a]) (Right [rv : ^b]))
(define (f) : Int
  (match (Left 1)
    [(Left _) 0]
    [(Right x) (let [_ (- x x)] 0)]))";
        var (program, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // Walk to the `(- x x)` apply and assert its arg type is concrete Int,
        // not a ZConstrainedVar / ZTypeVar.
        var fDefine = program.TopLevelForms.OfType<AstNode.Define>().Single(d => d.FnName == "f");
        var match = (AstNode.Match)fDefine.Body;
        var rightArm = match.Arms[1];
        var letBody = (AstNode.Let)rightArm.Body;
        var sub = (AstNode.Apply)letBody.Value;
        Assert.IsType<ZType.ZPrimitiveType>(sub.Args[0].ResolvedType);
        Assert.Equal(ZType.Int, sub.Args[0].ResolvedType);
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

    [Fact]
    public void Resolve_ObjectExprConstructorSuperArgs_ResolvesTypeVariables()
    {
        // Regression: Resolve walked an ObjectExpr's methods but skipped its
        // constructor — Names inside (super ...) kept their unresolved
        // ZTypeVar even after unification fixed them. Downstream IR/IL
        // emission then mapped the type to System.Object.
        // The free var must be bound polymorphically (e.g. by a match
        // pattern's constructor field) so that its ResolvedType is a
        // ZTypeVar at the point of inference and only gets bound to Int
        // by later unification.
        var source = @"
(union (Box ^a) (Wrap [v : ^a]))

(class #:open Cls
  [f0 : Int #:mutable]
  (define (Get) : Int f0))

(define (compute) : Int
  (match (Wrap 7)
    [(Wrap x51)
      (let [obj (object : Cls
        (constructor (super (let [y x51] y))))]
        x51)]))";

        var (program, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        // Walk the AST and assert no ResolvedType is still a ZTypeVar.
        AssertNoTypeVars(program);
    }

    [Fact]
    public void Resolve_ClassDeclConstructorSuperArgs_ResolvesTypeVariables()
    {
        var source = @"
(union (Box ^a) (Wrap [v : ^a]))

(class #:open Base
  [f0 : Int #:mutable]
  (define (Get) : Int f0))

(class Sub : Base
  [d0 : Int #:mutable]
  (constructor [n : Int]
    (super (match (Wrap n)
      [(Wrap m) (let [y m] y)]))))";

        var (program, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        AssertNoTypeVars(program);
    }

    [Fact]
    public void ObjectExprConstructorSuperArg_UnifiesAgainstBaseCtorParam()
    {
        // Regression (fuzzer case 0x1c03e27c): an ObjectExpr's (super ...)
        // args were inferred but never unified against the base class's
        // constructor parameter types. When a super arg's type was a
        // free type variable (e.g. ^b bound by a (Right_0 x) pattern in
        // a match where ^b is otherwise unconstrained) the variable
        // defaulted to System.Object. Both backends then emitted a
        // base(int, int, object) call against an (int, int, int) ctor,
        // producing unverifiable IL and uncompilable C#.
        var source = @"
(union (FUn ^a ^b) (Left [lv : ^a]) (Right [rv : ^b]))

(class #:open MyCls
  [f0 : Int #:mutable]
  (define (M [p : Int]) : Int p))

(define (compute) : Int
  (match (Left 1)
    [(Left x) x]
    [(Right y) (let [obj (object : MyCls
                            (constructor (super y))
                            (define (M [p : Int]) : Int p))] 0)]))";

        var (program, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        AssertNoTypeVars(program);
    }

    [Fact]
    public void ObjectExprConstructorSuperArg_TypeMismatch_IsRejected()
    {
        // The flip side: super args with concretely wrong types must now
        // produce a type error rather than silently emitting broken IL.
        var source = @"
(class #:open MyCls
  [f0 : Int #:mutable]
  (define (M [p : Int]) : Int p))

(define (compute) : Int
  (let [obj (object : MyCls
              (constructor (super ""hello""))
              (define (M [p : Int]) : Int p))] 0))";

        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors,
            "Expected a type error for passing String to an Int base ctor");
    }

    [Fact]
    public void ClassDeclConstructorSuperArg_TypeMismatch_IsRejected()
    {
        // Same fix applies to (class ... : Base (constructor (super ...))).
        var source = @"
(class #:open Base
  [f0 : Int #:mutable]
  (define (Get) : Int f0))

(class Sub : Base
  (constructor [s : String] (super s)))";

        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors,
            "Expected a type error for passing String to an Int base ctor");
    }

    [Fact]
    public void ObjectExprMethodBody_UnifiesAgainstReturnTypeAnnotation()
    {
        // Regression (fuzzer case 0xa16f555c): an ObjectExpr's method bodies were
        // inferred but the result was never unified with the method's declared
        // return type annotation. When the body's type was a free type variable
        // (e.g. ^b bound by a `(R y)` pattern in a match where ^b is otherwise
        // unconstrained), the variable defaulted to System.Object. The IL
        // backend then captured `y` as an `object` field on the anonymous class,
        // but the method signature still declared a concrete return type — so
        // `ldfld <object>; ret` failed verification with [StackUnexpected].
        var source = @"
(union (Either ^a ^b) (L [lv : ^a]) (R [rv : ^b]))

(interface IFoo
  (M [p : Int] : Int))

(define (test) : Int
  (match (L 42)
    [(L x) x]
    [(R y) (let [obj : IFoo (object IFoo
                              (define (M [p : Int]) : Int y))]
             (IFoo/M obj 0))]))";

        var (program, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        AssertNoTypeVars(program);
    }

    [Fact]
    public void ObjectExprMethodBody_TypeMismatch_IsRejected()
    {
        // The flip side: a body whose type is concretely incompatible with the
        // declared return type must produce a type error rather than silently
        // emitting broken IL.
        var source = @"
(interface IFoo
  (M [p : Int] : Int))

(define (test) : Int
  (let [obj : IFoo (object IFoo
                     (define (M [p : Int]) : Int ""hello""))]
    (IFoo/M obj 0)))";

        var (_, _, diag) = InferProgram(source);
        Assert.True(diag.HasErrors,
            "Expected a type error for a String body in an Int-returning method");
    }

    [Fact]
    public void Generalize_DoesNotPrematurelyGeneralizeOuterUnificationVar()
    {
        // Regression: a `let`-bound value inside a match arm used to be
        // generalized over type variables that were still free in the
        // surrounding match scrutinee's type. After generalization the let
        // body's use re-instantiated to a fresh var, so the outer constructor
        // type variable was never constrained by the body and ended up as
        // System.Object — the IL backend then produced an unverifiable
        // `rem` on a reference type. Found via the fuzzer (case 0x7f647d01).
        var source = @"
(union (Either ^a ^b) (Lt [v : ^a]) (Rt [v : ^b]))

(define (compute) : Int
  (match (Lt 5)
    [(Lt _) 1]
    [(Rt x) (let [y (if #t x x)] (% y 78))]))";
        var (program, _, diag) = InferProgram(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        AssertNoTypeVars(program);
    }

    [Fact]
    public void Substitution_Apply_DoesNotSubstituteThroughForAllBoundVars()
    {
        // Regression: Substitution.Apply on a ZForAllType used to walk into
        // the body without shielding the bound variable IDs. If the global
        // substitution later mapped a bound-var ID to another type, applying
        // the substitution to the generalized binding would leak that type
        // (as a free var) into what should have been a closed scheme. That
        // in turn caused the env-aware Generalize fix to over-subtract free
        // vars when generalizing later definitions.
        var sub = new Substitution();
        // A generalized scheme: forall t1. Func([t1], t1)
        var bound = new ZType.ZTypeVar(1);
        var scheme = new ZType.ZForAllType([1], new ZType.ZFuncType([bound], bound));
        // Pretend an unrelated unification mapped id 1 to Int.
        sub.Add(1, ZType.Int);
        var applied = sub.Apply(scheme);
        // The ForAll's body must still reference the bound var, not Int.
        Assert.IsType<ZType.ZForAllType>(applied);
        var fa = (ZType.ZForAllType)applied;
        Assert.Empty(Substitution.FreeVars(fa));
    }

    private static void AssertNoTypeVars(AstNode node)
    {
        if (node.ResolvedType is ZType.ZTypeVar tv)
            Assert.Fail($"Unresolved ZTypeVar #{tv.Id} on {node.GetType().Name}");

        switch (node)
        {
            case AstNode.Program p:
                foreach (var f in p.TopLevelForms) AssertNoTypeVars(f);
                break;
            case AstNode.ModuleDecl md:
                foreach (var f in md.Body) AssertNoTypeVars(f);
                break;
            case AstNode.Define d:
                AssertNoTypeVars(d.Body);
                break;
            case AstNode.DefineValue dv:
                AssertNoTypeVars(dv.Value);
                break;
            case AstNode.Let l:
                AssertNoTypeVars(l.Value);
                AssertNoTypeVars(l.Body);
                break;
            case AstNode.If i:
                AssertNoTypeVars(i.Condition);
                AssertNoTypeVars(i.Then);
                AssertNoTypeVars(i.Else);
                break;
            case AstNode.Lambda lam:
                AssertNoTypeVars(lam.Body);
                break;
            case AstNode.Apply app:
                AssertNoTypeVars(app.Function);
                foreach (var a in app.Args) AssertNoTypeVars(a);
                break;
            case AstNode.Match m:
                AssertNoTypeVars(m.Scrutinee);
                foreach (var arm in m.Arms) AssertNoTypeVars(arm.Body);
                break;
            case AstNode.ObjectExpr oe:
                foreach (var meth in oe.Methods) AssertNoTypeVars(meth.Body);
                if (oe.Constructor is { } oeCtor)
                {
                    if (oeCtor.SuperArgs is not null)
                        foreach (var a in oeCtor.SuperArgs) AssertNoTypeVars(a);
                    foreach (var (_, v) in oeCtor.FieldSets) AssertNoTypeVars(v);
                    foreach (var b in oeCtor.BodyExprs) AssertNoTypeVars(b);
                }
                break;
            case AstNode.ClassDecl cd:
                foreach (var meth in cd.Methods) AssertNoTypeVars(meth.Body);
                if (cd.Constructor is { } cdCtor)
                {
                    if (cdCtor.SuperArgs is not null)
                        foreach (var a in cdCtor.SuperArgs) AssertNoTypeVars(a);
                    foreach (var (_, v) in cdCtor.FieldSets) AssertNoTypeVars(v);
                    foreach (var b in cdCtor.BodyExprs) AssertNoTypeVars(b);
                }
                break;
        }
    }
}
