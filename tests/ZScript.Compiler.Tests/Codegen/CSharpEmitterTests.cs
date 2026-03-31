using Xunit;
using ZScript.Compiler.Codegen;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Ir;
using ZScript.Compiler.Pipeline;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Tests.Codegen;

public class CSharpEmitterTests
{
    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath(), ["zunit"] = GetZUnitPath() },
            ModuleSearchPaths = [GetZUnitPath()],
            ModuleAliases = new Dictionary<string, string> { ["zunit"] = "zunit/zunit" }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Diagnostics));
        var csResult = (CompilationResult.CSharpOutputResult)result;
        return csResult.CsOutput;
    }

    private static CompilationResult CompileResult(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath(), ["zunit"] = GetZUnitPath() },
            ModuleSearchPaths = [GetZUnitPath()],
            ModuleAliases = new Dictionary<string, string> { ["zunit"] = "zunit/zunit" }
        });
        return compilation.Compile(source);
    }

    private static (string Output, DiagnosticBag Diagnostics) EmitDirect(IrNode ir)
    {
        var diag = new DiagnosticBag();
        var emitter = new CSharpEmitter(diag, "TestNameSpace", "TestClass");
        var output = emitter.Emit(ir);
        return (output, diag);
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(CSharpEmitterTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScript.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static string GetZUnitPath()
    {
        var dir = Path.GetDirectoryName(typeof(CSharpEmitterTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScript.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "zunit", "src");
    }

    [Fact]
    public void EmitSimpleFunction()
    {
        var cs = Compile("(module test)\n(define (add [x : Int] [y : Int]) : Int (+ x y))");
        Assert.Contains("public static int Add(int x, int y)", cs);
        Assert.Contains("(x + y)", cs);
    }

    [Fact]
    public void EmitIfExpression()
    {
        var cs = Compile("(module test)\n(define (abs [x : Int]) : Int (if (< x 0) (- 0 x) x))");
        Assert.Contains("public static int Abs(int x)", cs);
        Assert.Contains("?", cs); // ternary operator
    }

    [Fact]
    public void EmitRecursiveFunction()
    {
        var source = @"(module test)
(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))";
        var cs = Compile(source);
        Assert.Contains("public static int Factorial(int n, int acc)", cs);
        // Should be rewritten to a while loop for TCO
        Assert.Contains("while (true)", cs);
    }

    [Fact]
    public void EmitLetBinding()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let [y (+ x 1)] (+ y 2)))");
        Assert.Contains("public static int F(int x)", cs);
    }

    [Fact]
    public void EmitBooleanExpression()
    {
        var cs = Compile("(module test)\n(define (check [a : Bool] [b : Bool]) : Bool (and a b))");
        Assert.Contains("&&", cs);
    }

    [Fact]
    public void EmitComparison()
    {
        var cs = Compile("(module test)\n(define (gt [a : Int] [b : Int]) : Bool (> a b))");
        Assert.Contains("(a > b)", cs);
    }

    [Fact]
    public void EmitNamespace()
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            Namespace = "MyGame.Logic"
        });
        var result = compilation.Compile("(module test)\n(define (id [x : Int]) : Int x)");
        Assert.True(result.Success);
        var csResult = (CompilationResult.CSharpOutputResult)result;
        Assert.Contains("namespace MyGame.Logic;", csResult.CsOutput);
    }

    [Fact]
    public void EmitMultipleFunctions()
    {
        var source = @"(module test)
(define (add [x : Int] [y : Int]) : Int (+ x y))
(define (dbl [x : Int]) : Int (add x x))";
        var cs = Compile(source);
        Assert.Contains("public static int Add(int x, int y)", cs);
        Assert.Contains("public static int Dbl(int x)", cs);
    }

    [Fact]
    public void EmitStringReturn()
    {
        var cs = Compile("(module test)\n(define (greet [name : String]) : String name)");
        Assert.Contains("public static string Greet(string name)", cs);
    }

    [Fact]
    public void EmitLetWithClrCallBody()
    {
        var source = @"
(import-clr
  [writeln System.Console/WriteLine])

(let [x ""hello""]
  (writeln x))";
        var cs = Compile(source);
        Assert.Contains("X = \"hello\"", cs);
        Assert.Contains("System.Console.WriteLine(X)", cs);
    }

    [Fact]
    public void EmitNestedLetWithClrCallBody()
    {
        var source = @"
(import-clr
  [writeln System.Console/WriteLine])

(let [x ""hello""]
  (let [y ""world""]
    (writeln y)))";
        var cs = Compile(source);
        Assert.Contains("X = \"hello\"", cs);
        Assert.Contains("Y = \"world\"", cs);
        Assert.Contains("System.Console.WriteLine(y)", cs);
    }

    [Fact]
    public void NamespaceDirectiveOverridesDefault()
    {
        var cs = Compile("(module test)\n(namespace My.Game.Logic)\n(define (id [x : Int]) : Int x)");
        Assert.Contains("namespace My.Game.Logic;", cs);
    }

    [Fact]
    public void NamespaceDirectiveOverridesCompilerOption()
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            Namespace = "From.Options"
        });
        var result = compilation.Compile("(module test)\n(namespace From.Source)\n(define (id [x : Int]) : Int x)");
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Diagnostics));
        var csResult = (CompilationResult.CSharpOutputResult)result;
        Assert.Contains("namespace From.Source;", csResult.CsOutput);
        Assert.DoesNotContain("From.Options", csResult.CsOutput);
    }

    [Fact]
    public void PipelineProducesValidOutput()
    {
        var source = @"(module test)
(define (square [x : Int]) : Int (* x x))";
        var compilation = new Compilation();
        var result = compilation.Compile(source);
        Assert.True(result.Success);
        var csResult = (CompilationResult.CSharpOutputResult)result;
        Assert.Contains("public static int Square(int x)", csResult.CsOutput);
    }

    [Fact]
    public void ModuleDecl_SetsClassName()
    {
        var cs = Compile("(module core)\n(define (id [x : Int]) : Int x)");
        Assert.Contains("public static class CoreModule", cs);
    }

    [Fact]
    public void ModuleDecl_HierarchicalName()
    {
        var cs = Compile("(module math/vector)\n(define (id [x : Int]) : Int x)");
        Assert.Contains("public static class Math_VectorModule", cs);
    }

    [Fact]
    public void ModuleDecl_HyphenatedName()
    {
        var cs = Compile("(module my-utils)\n(define (id [x : Int]) : Int x)");
        Assert.Contains("public static class MyUtilsModule", cs);
    }

    [Fact]
    public void NoModuleDecl_WithDefine_ReportsError()
    {
        var compilation = new Compilation(new CompilerOptions
            { OutputMode = OutputMode.CSharp, DisablePrelude = true });
        var result = compilation.Compile("(define (id [x : Int]) : Int x)");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Diagnostics, d => d.Message.Contains("require a (module ...) declaration"));
    }

    [Fact]
    public void EmitObjectExpr_SingleInterface()
    {
        var source = @"(module test)
(define (make-comparer) : IComparer
  (object IComparer
    (Compare [x : Int] [y : Int] : Int
      (- x y))))";
        var cs = Compile(source);
        Assert.Contains("class __Object_0 : IComparer", cs);
        Assert.Contains("public int Compare(int x, int y)", cs);
        Assert.Contains("new __Object_0()", cs);
    }

    [Fact]
    public void EmitObjectExpr_MultipleInterfaces()
    {
        var source = @"(module test)
(define (make-obj) : IFoo
  (object (IFoo IBar)
    (DoFoo : Int 42)
    (DoBar [x : Int] : Int x)))";
        var cs = Compile(source);
        Assert.Contains("class __Object_0 : IFoo, IBar", cs);
        Assert.Contains("public int DoFoo()", cs);
        Assert.Contains("public int DoBar(int x)", cs);
    }

    [Fact]
    public void EmitObjectExpr_WithBaseClass()
    {
        var source = @"(module test)
(class : open Animal
  [name : String]
  (Speak [] : String name))

(define (make-cat) : Animal
  (object : Animal
    (Speak [] : String ""meow"")))";
        var cs = Compile(source);
        Assert.Contains("class __Object_0 : Animal", cs);
        Assert.Contains("public override string Speak()", cs);
        Assert.Contains(": base()", cs);
    }

    [Fact]
    public void EmitObjectExpr_WithBaseClassAndInterface()
    {
        var source = @"(module test)
(interface ISerializable
  (Serialize [] : String))

(class : open Animal
  [name : String]
  (Speak [] : String name))

(define (make-cat) : Animal
  (object : Animal ISerializable
    (Speak [] : String ""meow"")
    (Serialize [] : String ""cat"")))";
        var cs = Compile(source);
        Assert.Contains("class __Object_0 : Animal, ISerializable", cs);
        Assert.Contains("public override string Speak()", cs);
    }

    [Fact]
    public void EmitObjectExpr_WithBaseClassAndConstructor()
    {
        var source = @"(module test)
(class : open Animal
  [name : String]
  [sound : String]
  (Speak [] : String name))

(define (make-cat) : Animal
  (object : Animal
    (constructor (super ""Cat"" ""meow""))
    (Speak [] : String ""I am a cat"")))";
        var cs = Compile(source);
        Assert.Contains("class __Object_0 : Animal", cs);
        Assert.Contains(": base(\"Cat\", \"meow\")", cs);
    }

    [Fact]
    public void EmitRecord_AppearsAfterPreambleNoProgramClass()
    {
        var cs = Compile("(record Point [x : Float] [y : Float])");
        var namespaceIdx = cs.IndexOf("namespace ");
        var recordIdx = cs.IndexOf("public sealed record Point(float X, float Y);");
        Assert.True(namespaceIdx >= 0, "namespace not found");
        Assert.True(recordIdx >= 0, "record declaration not found");
        Assert.True(namespaceIdx < recordIdx, "record should appear after namespace");
        Assert.DoesNotContain("public static class", cs);
    }

    [Fact]
    public void EmitUnion_AppearsAfterPreambleNoProgramClass()
    {
        var cs = Compile("(union Shape (Circle [r : Float]) (Rect [w : Float] [h : Float]))");
        var namespaceIdx = cs.IndexOf("namespace ");
        var unionIdx = cs.IndexOf("public abstract record Shape");
        Assert.True(namespaceIdx >= 0, "namespace not found");
        Assert.True(unionIdx >= 0, "union declaration not found");
        Assert.True(namespaceIdx < unionIdx, "union should appear after namespace");
        Assert.DoesNotContain("public static class", cs);
    }

    [Fact]
    public void EmitRecord_PreambleComesFirst()
    {
        var cs = Compile("(record Point [x : Int])");
        var preambleIdx = cs.IndexOf("// <auto-generated");
        var nullableIdx = cs.IndexOf("#nullable enable");
        var recordIdx = cs.IndexOf("public sealed record Point");
        Assert.True(preambleIdx >= 0);
        Assert.True(nullableIdx >= 0);
        Assert.True(recordIdx >= 0);
        Assert.True(preambleIdx < nullableIdx, "auto-generated comment should come before #nullable");
        Assert.True(nullableIdx < recordIdx, "#nullable should come before record");
    }

    [Fact]
    public void EmitRecordAndFunction_CorrectOrdering()
    {
        var source = @"(module test)
(record Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        var classIdx = cs.IndexOf("public static class ");
        var recordIdx = cs.IndexOf("public sealed record Point(int X, int Y);");
        var funcIdx = cs.IndexOf("public static Point Origin()");
        Assert.True(classIdx >= 0, "module class not found");
        Assert.True(recordIdx >= 0, "record declaration not found");
        Assert.True(funcIdx >= 0, "function not found");
        Assert.True(classIdx < recordIdx, "record should appear inside class (after class opening)");
        Assert.True(classIdx < funcIdx, "function should appear inside class (after class opening)");
    }

    [Fact]
    public void EmitClassDeclOnly_NoProgramClass()
    {
        var source = @"
(class Point
  [x : Int]
  [y : Int]
  (magnitude [] : Int
    (+ (* x x) (* y y))))";
        var cs = Compile(source);
        Assert.Contains("public sealed class Point", cs);
        Assert.DoesNotContain("public static class", cs);
    }

    [Fact]
    public void EmitClassDecl_OpenClass_NotSealed()
    {
        var source = @"
(class : open Animal
  [name : String]
  (Speak [] : String name))";
        var cs = Compile(source);
        Assert.Contains("public class Animal", cs);
        Assert.DoesNotContain("sealed class Animal", cs);
        Assert.Contains("public virtual string Speak()", cs);
    }

    [Fact]
    public void EmitClassDecl_Inheritance_BaseClassInList()
    {
        var source = @"
(class : open Animal
  [name : String])

(class Dog : Animal
  [breed : String])";
        var cs = Compile(source);
        Assert.Contains("public sealed class Dog : Animal", cs);
        Assert.Contains("public Dog(string Name, string Breed) : base(Name)", cs);
    }

    [Fact]
    public void EmitClassDecl_Inheritance_OverrideMethod()
    {
        var source = @"
(class : open Animal
  [name : String]
  (Speak [] : String name))

(class Dog : Animal
  [breed : String]
  (Speak [] : String breed))";
        var cs = Compile(source);
        Assert.Contains("public virtual string Speak()", cs);
        Assert.Contains("public override string Speak()", cs);
    }

    [Fact]
    public void EmitClassDecl_Inheritance_SuperMethodCall()
    {
        var source = @"
(class : open Animal
  [name : String]
  (Speak [] : String name))

(class Dog : Animal
  (Speak [] : String
    (string-append (super/Speak) ""!"")))";
        var cs = Compile(source);
        Assert.Contains("base.Speak()", cs);
    }

    [Fact]
    public void EmitClassDecl_Inheritance_BaseClassAndInterface()
    {
        var source = @"
(interface IService
  (Name [] : String))

(class : open Base
  [name : String]
  (Name [] : String name))

(class Impl : Base IService)";
        var cs = Compile(source);
        Assert.Contains("public sealed class Impl : Base, IService", cs);
    }

    [Fact]
    public void EmitClassDecl_ExplicitConstructor_WithSuper()
    {
        var source = @"
(class : open Animal
  [name : String]
  (Speak [] : String name))

(class Dog : Animal
  [breed : String]
  (constructor [nickname : String]
    (super nickname)
    (set! breed ""mixed"")))";
        var cs = Compile(source);
        Assert.Contains("public Dog(string nickname) : base(nickname)", cs);
        Assert.Contains("this.Breed = \"mixed\"", cs);
    }

    [Fact]
    public void EmitClassDecl_ExplicitConstructor_NoBase()
    {
        var source = @"
(class Widget
  [name : String]
  [size : Int]
  (constructor [n : String]
    (set! name n)
    (set! size 0)))";
        var cs = Compile(source);
        Assert.Contains("public Widget(string n)", cs);
        Assert.Contains("this.Name = n;", cs);
        Assert.Contains("this.Size = 0;", cs);
    }

    [Fact]
    public void EmitMatch_WildcardArm_NoFallback()
    {
        var source = @"(module test)
(union Color (Red) (Green) (Blue))
(define (name [c : Color]) : Int
  (match c
    [(Red) 1]
    [(Green) 2]
    [_ 3]))";
        var cs = Compile(source);
        Assert.DoesNotContain("throw new System.InvalidOperationException", cs);
    }

    [Fact]
    public void EmitMatch_VariableArm_NoFallback()
    {
        var source = @"(module test)
(define (describe [x : Int]) : Int
  (match x
    [0 0]
    [other other]))";
        var cs = Compile(source);
        Assert.DoesNotContain("throw new System.InvalidOperationException", cs);
    }

    [Fact]
    public void EmitClrNew_NoArgs()
    {
        var cs = Compile("(let [obj (new System.Object)] obj)");
        Assert.Contains("new System.Object()", cs);
    }

    [Fact]
    public void EmitClrNew_WithArgs()
    {
        var cs = Compile("(let [lst (new System.Collections.ArrayList 10)] lst)");
        Assert.Contains("new System.Collections.ArrayList(10)", cs);
    }

    [Fact]
    public void EmitLetInFuncBody_EmitsVarDeclaration()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let [y (+ x 1)] (+ y 2)))");
        Assert.Contains("var y =", cs);
        Assert.DoesNotContain("System.Func<", cs);
    }

    [Fact]
    public void EmitLetStarInFuncBody_EmitsVarDeclarations()
    {
        var cs = Compile(
            "(module test)\n(define (f [a : Int] [b : Int]) : Int (let* ([x (* a 2)] [y (+ x b)]) (+ x y)))");
        Assert.Contains("var x =", cs);
        Assert.Contains("var y =", cs);
        Assert.DoesNotContain("System.Func<", cs);
    }

    [Fact]
    public void EmitLetWithShadowing_StillUsesIIFE()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let* ([x (+ x 1)] [x (* x 2)]) x))");
        Assert.Contains("System.Func<", cs);
    }

    [Fact]
    public void EmitTestCase_SingleAssertion()
    {
        var source = @"(module test)
(import zunit)
(import-clr
  [check-true Xunit.Assert/True])

(test-case booleans-work
  (check-true #t))";
        var cs = Compile(source);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("public static", cs);
        Assert.Contains("BooleansWork()", cs);
        Assert.Contains("Xunit.Assert.True(true)", cs);
    }

    [Fact]
    public void EmitTestCase_MultipleAssertions()
    {
        var source = @"(module test)
(import zunit)
(import-clr
  [check-equal Xunit.Assert/Equal ^a]
  [check-true  Xunit.Assert/True])

(test-case multiple-checks
  (check-equal 1 1)
  (check-true #t))";
        var cs = Compile(source);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("public static", cs);
        Assert.Contains("MultipleChecks()", cs);
        Assert.Contains("Xunit.Assert.Equal(1, 1)", cs);
        Assert.Contains("Xunit.Assert.True(true)", cs);
    }

    [Fact]
    public void EmitTestCase_WithExpression()
    {
        var source = @"(module test)
(import zunit)
(import-clr
  [check-equal Xunit.Assert/Equal ^a])

(test-case addition-works
  (check-equal (+ 1 2) 3))";
        var cs = Compile(source);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("public static", cs);
        Assert.Contains("AdditionWorks()", cs);
        Assert.Contains("Xunit.Assert.Equal((1 + 2), 3)", cs);
    }

    [Fact]
    public void EmitTestCase_CoexistsWithDefine()
    {
        var source = @"(module test)
(import zunit)
(import-clr
  [check-equal Xunit.Assert/Equal ^a])

(define (add [x : Int] [y : Int]) : Int (+ x y))

(test-case add-works
  (check-equal (add 1 2) 3))";
        var cs = Compile(source);
        Assert.Contains("public static int Add(int x, int y)", cs);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("public static", cs);
        Assert.Contains("AddWorks()", cs);
    }

    [Fact]
    public void EmitRaiseExpression()
    {
        var cs = Compile(@"(module test)
(define (fail) : Int
  (raise (new System.Exception ""boom"")))");
        Assert.Contains("throw new System.Exception(\"boom\")", cs);
        // Must NOT contain "return throw"
        Assert.DoesNotContain("return throw", cs);
    }

    [Fact]
    public void EmitRaiseInIfBranch()
    {
        var cs = Compile(@"(module test)
(define (check [x : Int]) : Int
  (if (> x 0) x (raise (new System.ArgumentException ""negative""))))");
        Assert.Contains("throw new System.ArgumentException(\"negative\")", cs);
    }

    [Fact]
    public void EmitRaiseInFunctionBody()
    {
        var cs = Compile(@"(module test)
(define (not-implemented) : Int
  (raise (new System.NotImplementedException ""todo"")))");
        Assert.Contains("throw new System.NotImplementedException(\"todo\")", cs);
        Assert.DoesNotContain("return throw", cs);
    }

    [Fact]
    public void EmitAsyncFunction()
    {
        var cs = Compile("(module test)\n(define-async (compute [x : Int]) : (Task Int) (+ x 1))");
        Assert.Contains("async", cs);
        Assert.Contains("System.Threading.Tasks.Task<int>", cs);
        Assert.Contains("Compute", cs);
    }

    [Fact]
    public void EmitAwaitExpression()
    {
        var source = @"(module test)
(define-async (compute [x : Int]) : (Task Int) (+ x 1))
(define-async (use-it [x : Int]) : (Task Int) (await (compute x)))";
        var cs = Compile(source);
        Assert.Contains("await", cs);
        Assert.Contains("Compute(x)", cs);
    }

    [Fact]
    public void EmitAwaitInLet_EmitsVarStatement_NotLambda()
    {
        var source = @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int)
  (let [result (await (inner x))]
    (+ result 10)))";
        var cs = Compile(source);
        // Must emit as var statement, not an IIFE lambda
        Assert.Contains("var result = await Inner(x);", cs);
        Assert.DoesNotContain("System.Func", cs.Split("Outer")[1]);
    }

    [Fact]
    public void EmitAwait_NoWrappingParens()
    {
        var source = @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int) (await (inner x)))";
        var cs = Compile(source);
        // await should not be wrapped in parens: "await inner(x)" not "(await inner(x))"
        Assert.Contains("await Inner(x)", cs);
        Assert.DoesNotContain("(await Inner(x))", cs);
    }

    [Fact]
    public void EmitAsyncNonGenericTask_NoReturnStatement()
    {
        var source = @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (do-work) : Task (await (inner 42)))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task DoWork()", cs);
        // Non-generic Task method must not have "return await ..."
        Assert.DoesNotContain("return await", cs);
        Assert.Contains("await Inner(42);", cs);
    }

    [Fact]
    public void EmitNestedLetWithAwait_EmitsSequentialStatements()
    {
        var source = @"(module test)
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (chain [x : Int]) : (Task Int)
  (let [a (await (step x))]
    (let [b (await (step a))]
      (+ a b))))";
        var cs = Compile(source);
        Assert.Contains("var a = await Step(x);", cs);
        Assert.Contains("var b = await Step(a);", cs);
        Assert.Contains("return (a + b);", cs);
    }

    [Fact]
    public void EmitAwaitInIfBranches()
    {
        var source = @"(module test)
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (choose [flag : Bool] [x : Int]) : (Task Int)
  (if flag (await (step x)) (await (step 0))))";
        var cs = Compile(source);
        Assert.Contains("async", cs);
        // Both branches should contain await calls
        Assert.Contains("await Step(x)", cs);
        Assert.Contains("await Step(0)", cs);
    }

    [Fact]
    public void EmitAsyncWithoutAwait_EmitsReturn()
    {
        var source = @"(module test)
(define-async (simple [x : Int]) : (Task Int) (+ x 1))";
        var cs = Compile(source);
        // Async without await still emits a return
        Assert.Contains("return (x + 1);", cs);
    }

    [Fact]
    public void EmitAwaitNonGenericTaskInLet()
    {
        var source = @"(module test)
(define-async (side-effect) : Task 0)
(define-async (use-it) : (Task Int)
  (let [_ (await (side-effect))]
    42))";
        var cs = Compile(source);
        // The let binding with await should emit var _ = await side_effect();
        Assert.Contains("var _ = await SideEffect();", cs);
        Assert.Contains("return 42;", cs);
    }

    [Fact]
    public void EmitGenericIdentityFunction()
    {
        var cs = Compile("(module test)\n(define (id [x : ^a]) : ^a x)");
        Assert.Contains("public static T0 Id<T0>(T0 x)", cs);
    }

    [Fact]
    public void EmitGenericMultiTypeParams()
    {
        var cs = Compile("(module test)\n(define (const [x : ^a] [y : ^b]) : ^a x)");
        Assert.Contains("<T0, T1>", cs);
        Assert.Contains("T0 x", cs);
        Assert.Contains("T1 y", cs);
    }

    [Fact]
    public void EmitGenericHigherOrderFunction()
    {
        var cs = Compile("(module test)\n(define (apply [f : (Fn [^a] ^b)] [x : ^a]) : ^b (f x))");
        Assert.Contains("System.Func<T0, T1> f", cs);
        Assert.Contains("<T0, T1>", cs);
    }

    [Fact]
    public void EmitGenericWithCollectionType()
    {
        var cs = Compile("(module test)\n(define (wrap [x : ^a]) : (List ^a) (list x))");
        Assert.Contains("ImmutableList<T0> Wrap<T0>(T0 x)", cs);
    }

    [Fact]
    public void EmitMonomorphicFunctionHasNoTypeParams()
    {
        var cs = Compile("(module test)\n(define (add [x : Int] [y : Int]) : Int (+ x y))");
        Assert.DoesNotContain("<T", cs);
    }

    [Fact]
    public void EmitExpr_UnhandledNodeType_ReportsError()
    {
        // TypeTest is an IR node type not handled by CSharpEmitter's EmitExpr switch
        var typeTest = new IrNode.TypeTest(
            new IrNode.Var("x") { Type = ZType.Int },
            "SomeType",
            "bound") { Type = ZType.Bool };

        // Wrap in a Seq with a FuncDef that uses the unhandled node
        var funcDef = new IrNode.FuncDef(
            "test_func",
            [new IrParam("x", ZType.Int)],
            ZType.Bool,
            typeTest,
            IsSelfRecursive: false);

        var seq = new IrNode.Seq([funcDef]);
        var (_, diag) = EmitDirect(seq);

        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics,
            d => d.IsError && d.Message.Contains("C# emission not implemented for"));
    }

    [Fact]
    public void EmitExpr_HandledNodeType_NoError()
    {
        var result = CompileResult("(module test)\n(define (add [x : Int] [y : Int]) : Int (+ x y))");
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics.Diagnostics,
            d => d.Message.Contains("C# emission not implemented"));
    }

    [Fact]
    public void EmitTryCatch_NonResultType_ReportsWarning()
    {
        // TryCatch with a non-Result type should trigger the fallback warning
        var tryCatch = new IrNode.TryCatch(new IrNode.IntConst(42) { Type = ZType.Int })
        {
            Type = ZType.Int // Not a Result type
        };

        var funcDef = new IrNode.FuncDef(
            "test_func",
            [],
            ZType.Int,
            tryCatch,
            IsSelfRecursive: false);

        var seq = new IrNode.Seq([funcDef]);
        var (_, diag) = EmitDirect(seq);

        Assert.Contains(diag.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning
                 && d.Message.Contains("Expected Result type for try-catch expression"));
    }

    [Fact]
    public void EmitTryCatch_WithResultType_NoWarning()
    {
        // Construct a TryCatch with a proper Result type directly
        var resultType = new ZType.ZNamedType("Result", [ZType.Int, new ZType.ZNamedType("Error", [])]);
        var tryCatch = new IrNode.TryCatch(new IrNode.IntConst(42) { Type = ZType.Int })
        {
            Type = resultType
        };

        var funcDef = new IrNode.FuncDef(
            "test_func",
            [],
            resultType,
            tryCatch,
            IsSelfRecursive: false);

        var seq = new IrNode.Seq([funcDef]);
        var (_, diag) = EmitDirect(seq);

        Assert.DoesNotContain(diag.Diagnostics,
            d => d.Message.Contains("Expected Result type for try-catch expression"));
    }

    [Fact]
    public void TypeToCs_UnresolvedTypeVar_ReportsWarning()
    {
        // A ZTypeVar that is not in any type parameter map should trigger a warning
        var unresolvedVar = new IrNode.Var("x") { Type = new ZType.ZTypeVar(999) };

        var funcDef = new IrNode.FuncDef(
            "test_func",
            [new IrParam("x", new ZType.ZTypeVar(999))],
            new ZType.ZTypeVar(999),
            unresolvedVar,
            IsSelfRecursive: false);

        var seq = new IrNode.Seq([funcDef]);
        var (output, diag) = EmitDirect(seq);

        Assert.Contains(diag.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning
                 && d.Message.Contains("Unresolved type variable in C# emission"));
        Assert.Contains("object", output);
    }

    [Fact]
    public void ValidCompilation_NoSpuriousWarnings()
    {
        var source = @"(module test)
(define (add [x : Int] [y : Int]) : Int (+ x y))
(define (greet [name : String]) : String name)
(define (check [a : Bool]) : Bool (not a))";
        var result = CompileResult(source);
        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics.Diagnostics);
    }

    // ─── Nested Type Declaration Tests ──────────────────────────────────

    [Fact]
    public void EmitRecordInModule_NestedInsideModuleClass()
    {
        var source = @"(module test)
(record Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        var classIdx = cs.IndexOf("public static class TestModule");
        var recordIdx = cs.IndexOf("public sealed record Point(int X, int Y);");
        var closingBraceIdx = cs.LastIndexOf('}');
        Assert.True(classIdx >= 0, "module class not found");
        Assert.True(recordIdx >= 0, "record declaration not found");
        Assert.True(classIdx < recordIdx, "record should be nested inside module class");
        Assert.True(recordIdx < closingBraceIdx, "record should be before closing brace");
    }

    [Fact]
    public void EmitUnionInModule_NestedInsideModuleClass()
    {
        var source = @"(module test)
(union Shape (Circle [r : Float]) (Rect [w : Float] [h : Float]))
(define (unit-circle) : Shape (Circle 1.0))";
        var cs = Compile(source);
        var classIdx = cs.IndexOf("public static class TestModule");
        var unionIdx = cs.IndexOf("public abstract record Shape");
        Assert.True(classIdx >= 0, "module class not found");
        Assert.True(unionIdx >= 0, "union declaration not found");
        Assert.True(classIdx < unionIdx, "union should be nested inside module class");
    }

    [Fact]
    public void EmitClassInModule_NestedInsideModuleClass()
    {
        var source = @"(module test)
(class Point
  [x : Int]
  [y : Int]
  (magnitude [] : Int
    (+ (* x x) (* y y))))
(define (make-point) : Point (Point 1 2))";
        var cs = Compile(source);
        var moduleClassIdx = cs.IndexOf("public static class TestModule");
        var classIdx = cs.IndexOf("public sealed class Point");
        Assert.True(moduleClassIdx >= 0, "module class not found");
        Assert.True(classIdx >= 0, "class declaration not found");
        Assert.True(moduleClassIdx < classIdx, "class should be nested inside module class");
    }

    [Fact]
    public void EmitInterfaceInModule_NestedInsideModuleClass()
    {
        var source = @"(module test)
(interface IGreeter
  (greet [name : String] : String))
(define (make-greeter) : IGreeter
  (object (IGreeter)
    (greet [name : String] : String name)))";
        var cs = Compile(source);
        var moduleClassIdx = cs.IndexOf("public static class TestModule");
        var ifaceIdx = cs.IndexOf("public interface IGreeter");
        Assert.True(moduleClassIdx >= 0, "module class not found");
        Assert.True(ifaceIdx >= 0, "interface declaration not found");
        Assert.True(moduleClassIdx < ifaceIdx, "interface should be nested inside module class");
    }

    [Fact]
    public void EmitRecordWithoutModule_StaysAtNamespaceLevel()
    {
        var cs = Compile("(record Point [x : Int] [y : Int])");
        var recordIdx = cs.IndexOf("public sealed record Point(int X, int Y);");
        Assert.True(recordIdx >= 0, "record declaration not found");
        Assert.DoesNotContain("public static class", cs);
    }

    [Fact]
    public void EmitTypeOnlyModule_EmitsModuleClass()
    {
        var source = @"(module test)
(record Point [x : Int] [y : Int])";
        var cs = Compile(source);
        var classIdx = cs.IndexOf("public static class TestModule");
        var recordIdx = cs.IndexOf("public sealed record Point(int X, int Y);");
        Assert.True(classIdx >= 0, "module class should be emitted even for type-only modules");
        Assert.True(recordIdx >= 0, "record declaration not found");
        Assert.True(classIdx < recordIdx, "record should be nested inside module class");
    }

    [Fact]
    public void EmitVariadicFunction_EmitsParamsKeyword()
    {
        var source = @"(define (fmt [s : String] [args : String ...]) : String s)";
        var cs = Compile(source);
        Assert.Contains("params string[] args", cs);
    }

    [Fact]
    public void EmitVariadicCall_EmitsArrayConstruction()
    {
        var source = @"
(define (fmt [s : String] [args : String ...]) : String s)
(fmt ""hello"" ""a"" ""b"")";
        var cs = Compile(source);
        Assert.Contains("new string[]", cs);
    }
}
