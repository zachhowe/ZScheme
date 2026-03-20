namespace ZScript.Compiler.Tests.Codegen;

using ZScript.Compiler.Pipeline;
using Xunit;

public class CSharpEmitterTests
{
    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions { OutputMode = OutputMode.CSharp });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Diagnostics));
        return result.Output!;
    }

    [Fact]
    public void EmitSimpleFunction()
    {
        var cs = Compile("(define (add [x : Int] [y : Int]) : Int (+ x y))");
        Assert.Contains("public static int add(int x, int y)", cs);
        Assert.Contains("(x + y)", cs);
    }

    [Fact]
    public void EmitIfExpression()
    {
        var cs = Compile("(define (abs [x : Int]) : Int (if (< x 0) (- 0 x) x))");
        Assert.Contains("public static int abs(int x)", cs);
        Assert.Contains("?", cs); // ternary operator
    }

    [Fact]
    public void EmitRecursiveFunction()
    {
        var source = @"(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))";
        var cs = Compile(source);
        Assert.Contains("public static int factorial(int n, int acc)", cs);
        // Should be rewritten to a while loop for TCO
        Assert.Contains("while (true)", cs);
    }

    [Fact]
    public void EmitLetBinding()
    {
        var cs = Compile("(define (f [x : Int]) : Int (let [y (+ x 1)] (+ y 2)))");
        Assert.Contains("public static int f(int x)", cs);
    }

    [Fact]
    public void EmitBooleanExpression()
    {
        var cs = Compile("(define (check [a : Bool] [b : Bool]) : Bool (and a b))");
        Assert.Contains("&&", cs);
    }

    [Fact]
    public void EmitComparison()
    {
        var cs = Compile("(define (gt [a : Int] [b : Int]) : Bool (> a b))");
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
        var result = compilation.Compile("(define (id [x : Int]) : Int x)");
        Assert.True(result.Success);
        Assert.Contains("namespace MyGame.Logic;", result.Output!);
    }

    [Fact]
    public void EmitMultipleFunctions()
    {
        var source = @"
(define (add [x : Int] [y : Int]) : Int (+ x y))
(define (dbl [x : Int]) : Int (add x x))";
        var cs = Compile(source);
        Assert.Contains("public static int add(int x, int y)", cs);
        Assert.Contains("public static int dbl(int x)", cs);
    }

    [Fact]
    public void EmitStringReturn()
    {
        var cs = Compile("(define (greet [name : String]) : String name)");
        Assert.Contains("public static string greet(string name)", cs);
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
        Assert.Contains("x = \"hello\"", cs);
        Assert.Contains("System.Console.WriteLine(x)", cs);
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
        Assert.Contains("x = \"hello\"", cs);
        Assert.Contains("y = \"world\"", cs);
        Assert.Contains("System.Console.WriteLine(y)", cs);
    }

    [Fact]
    public void NamespaceDirectiveOverridesDefault()
    {
        var cs = Compile("(namespace My.Game.Logic)\n(define (id [x : Int]) : Int x)");
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
        var result = compilation.Compile("(namespace From.Source)\n(define (id [x : Int]) : Int x)");
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Diagnostics));
        Assert.Contains("namespace From.Source;", result.Output!);
        Assert.DoesNotContain("From.Options", result.Output!);
    }

    [Fact]
    public void PipelineProducesValidOutput()
    {
        var source = @"(define (square [x : Int]) : Int (* x x))";
        var compilation = new Compilation();
        var result = compilation.Compile(source);
        Assert.True(result.Success);
        Assert.NotNull(result.Output);
        Assert.Contains("public static int square(int x)", result.Output);
    }

    [Fact]
    public void ModuleDecl_SetsClassName()
    {
        var cs = Compile("(module core)\n(define (id [x : Int]) : Int x)");
        Assert.Contains("public static class Core", cs);
    }

    [Fact]
    public void ModuleDecl_HierarchicalName()
    {
        var cs = Compile("(module math/vector)\n(define (id [x : Int]) : Int x)");
        Assert.Contains("public static class MathVector", cs);
    }

    [Fact]
    public void ModuleDecl_HyphenatedName()
    {
        var cs = Compile("(module my-utils)\n(define (id [x : Int]) : Int x)");
        Assert.Contains("public static class MyUtils", cs);
    }

    [Fact]
    public void NoModuleDecl_DefaultsToProgram()
    {
        var cs = Compile("(define (id [x : Int]) : Int x)");
        Assert.Contains("public static class Program", cs);
    }

    [Fact]
    public void EmitObjectExpr_SingleInterface()
    {
        var source = @"
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
        var source = @"
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
    public void EmitRecord_AppearsAfterPreambleBeforeClass()
    {
        var cs = Compile("(record Point [x : Float] [y : Float])");
        var namespaceIdx = cs.IndexOf("namespace ");
        var recordIdx = cs.IndexOf("public sealed record Point(float x, float y);");
        var classIdx = cs.IndexOf("public static class ");
        Assert.True(namespaceIdx >= 0, "namespace not found");
        Assert.True(recordIdx >= 0, "record declaration not found");
        Assert.True(classIdx >= 0, "class declaration not found");
        Assert.True(namespaceIdx < recordIdx, "record should appear after namespace");
        Assert.True(recordIdx < classIdx, "record should appear before class");
    }

    [Fact]
    public void EmitUnion_AppearsAfterPreambleBeforeClass()
    {
        var cs = Compile("(union Shape (Circle [r : Float]) (Rect [w : Float] [h : Float]))");
        var namespaceIdx = cs.IndexOf("namespace ");
        var unionIdx = cs.IndexOf("public abstract record Shape;");
        var classIdx = cs.IndexOf("public static class ");
        Assert.True(namespaceIdx >= 0, "namespace not found");
        Assert.True(unionIdx >= 0, "union declaration not found");
        Assert.True(classIdx >= 0, "class declaration not found");
        Assert.True(namespaceIdx < unionIdx, "union should appear after namespace");
        Assert.True(unionIdx < classIdx, "union should appear before class");
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
        var source = @"
(record Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        var namespaceIdx = cs.IndexOf("namespace ");
        var recordIdx = cs.IndexOf("public sealed record Point(int x, int y);");
        var classIdx = cs.IndexOf("public static class ");
        var funcIdx = cs.IndexOf("public static Point origin()");
        Assert.True(namespaceIdx < recordIdx, "record should appear after namespace");
        Assert.True(recordIdx < classIdx, "record should appear before class");
        Assert.True(classIdx < funcIdx, "function should appear inside class (after class opening)");
    }

    [Fact]
    public void EmitMatch_WildcardArm_NoFallback()
    {
        var source = @"
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
        var source = @"
(define (describe [x : Int]) : Int
  (match x
    [0 0]
    [other other]))";
        var cs = Compile(source);
        Assert.DoesNotContain("throw new System.InvalidOperationException", cs);
    }

    [Fact]
    public void EmitLetExpr_WrapsInFuncDelegate()
    {
        var cs = Compile("(define (f [x : Int]) : Int (let [y (+ x 1)] (+ y 2)))");
        Assert.Contains("System.Func<", cs);
    }

    [Fact]
    public void EmitTestCase_SingleAssertion()
    {
        var source = @"
(import-clr
  [check-true ZScript.ZUnit.ZsAssert/IsTrue])

(test-case ""booleans work""
  (check-true true))";
        var cs = Compile(source);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("public static void booleans_work()", cs);
        Assert.Contains("ZScript.ZUnit.ZsAssert.IsTrue(true)", cs);
    }

    [Fact]
    public void EmitTestCase_MultipleAssertions()
    {
        var source = @"
(import-clr
  [check-equal ZScript.ZUnit.ZsAssert/EqualInt]
  [check-true  ZScript.ZUnit.ZsAssert/IsTrue])

(test-case ""multiple checks""
  (check-equal 1 1)
  (check-true true))";
        var cs = Compile(source);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("public static void multiple_checks()", cs);
        Assert.Contains("ZScript.ZUnit.ZsAssert.EqualInt(1, 1)", cs);
        Assert.Contains("ZScript.ZUnit.ZsAssert.IsTrue(true)", cs);
    }

    [Fact]
    public void EmitTestCase_WithExpression()
    {
        var source = @"
(import-clr
  [check-equal ZScript.ZUnit.ZsAssert/EqualInt])

(test-case ""addition works""
  (check-equal (+ 1 2) 3))";
        var cs = Compile(source);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("public static void addition_works()", cs);
        Assert.Contains("ZScript.ZUnit.ZsAssert.EqualInt((1 + 2), 3)", cs);
    }

    [Fact]
    public void EmitTestCase_CoexistsWithDefine()
    {
        var source = @"
(import-clr
  [check-equal ZScript.ZUnit.ZsAssert/EqualInt])

(define (add [x : Int] [y : Int]) : Int (+ x y))

(test-case ""add works""
  (check-equal (add 1 2) 3))";
        var cs = Compile(source);
        Assert.Contains("public static int add(int x, int y)", cs);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("public static void add_works()", cs);
    }
}
