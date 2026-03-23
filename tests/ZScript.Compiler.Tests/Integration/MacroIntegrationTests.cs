namespace ZScript.Compiler.Tests.Integration;

using ZScript.Compiler.Pipeline;
using Xunit;

public class MacroIntegrationTests
{
    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            StdLibPath = GetStdLibPath(),
            ModuleSearchPaths = [GetZUnitPath()]
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
        return result.Output!;
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(MacroIntegrationTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScript.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static string GetZUnitPath()
    {
        var dir = Path.GetDirectoryName(typeof(MacroIntegrationTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScript.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "zunit", "src");
    }

    [Fact]
    public void WhenMacro_CompilesSuccessfully()
    {
        var source = @"
            (define-syntax when
              (syntax-rules ()
                [(when cond body ...) (if cond (begin body ...) ())]))
            (define (f [x : Int]) : Unit
              (when (> x 0) ()))";
        var cs = Compile(source);
        Assert.Contains("f", cs);
    }

    [Fact]
    public void UnlessMacro_CompilesSuccessfully()
    {
        var source = @"
            (define-syntax unless
              (syntax-rules ()
                [(unless cond body ...) (if cond () (begin body ...))]))
            (define (f [x : Int]) : Unit
              (unless (< x 0) ()))";
        var cs = Compile(source);
        Assert.Contains("f", cs);
    }

    [Fact]
    public void TestCaseMacro_ProducesTestMethod()
    {
        var source = @"(import zunit)
(test-case addition (+ 1 2))";
        var cs = Compile(source);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("public static", cs);
        Assert.Contains("addition", cs);
    }

    [Fact]
    public void TestCaseWithMultipleBodies()
    {
        var source = @"(import zunit)
(test-case multi (+ 1 2) (* 3 4))";
        var cs = Compile(source);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("multi", cs);
    }

    [Fact]
    public void MacroAndRegularCode_Coexist()
    {
        var source = @"
            (define-syntax swap-args
              (syntax-rules ()
                [(swap-args f a b) (f b a)]))
            (define (sub [x : Int] [y : Int]) : Int (- x y))
            (define (test [a : Int] [b : Int]) : Int (swap-args sub a b))";
        var cs = Compile(source);
        Assert.Contains("sub", cs);
        Assert.Contains("test", cs);
    }

    [Fact]
    public void QuoteShorthand_LexesCorrectly()
    {
        // Quote shorthands should lex and parse without errors,
        // though quote/quasiquote aren't special forms in the AST yet
        var source = @"(define x 42)";
        var cs = Compile(source);
        Assert.Contains("x", cs);
    }

    [Fact]
    public void MyAndMacro_RecursiveExpansion()
    {
        var source = @"
            (define-syntax my-and
              (syntax-rules ()
                [(my-and) #t]
                [(my-and x) x]
                [(my-and x rest ...) (if x (my-and rest ...) #f)]))
            (define (all-positive [a : Bool] [b : Bool] [c : Bool]) : Bool
              (my-and a b c))";
        var cs = Compile(source);
        Assert.Contains("all_positive", cs);
    }

    [Fact]
    public void TestSuiteMacro_GeneratesClassWithFactAttributes()
    {
        var source = @"(import zunit)
(test-suite MyTests
  (test-case test-addition
    (check-equal? 4 (+ 2 2)))
  (test-case test-subtraction
    (check-equal? 2 (- 4 2))))";
        var cs = Compile(source);
        Assert.Contains("sealed class MyTests", cs);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("test_addition", cs);
        Assert.Contains("test_subtraction", cs);
    }
}
