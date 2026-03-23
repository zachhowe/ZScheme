namespace ZScript.StdLib.Tests;

using ZScript.Compiler.Pipeline;
using Xunit;

/// <summary>
/// Tests that stdlib ZScript modules (option, result, error) compile successfully
/// via the prelude auto-import system.
/// </summary>
public class StdLibCompilationTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(StdLibCompilationTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScript.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "src", "ZScript.StdLib");
    }

    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            StdLibPath = GetStdLibPath()
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
        return result.Output!;
    }

    [Fact]
    public void PreludeOption_SomeNone_Available()
    {
        var cs = Compile("(define (f [x : Int]) : (Option Int) (if (> x 0) (Some x) None))");
        Assert.Contains("Some", cs);
        Assert.Contains("None", cs);
    }

    [Fact]
    public void PreludeResult_OkErr_Available()
    {
        var cs = Compile("(define (f [x : Int]) : (Result Int ErrorInfo) (if (> x 0) (Ok x) (Err (Error \"bad\"))))");
        Assert.Contains("Ok", cs);
        Assert.Contains("Err", cs);
    }

    [Fact]
    public void PreludeOption_MatchWorks()
    {
        var cs = Compile(@"
(define (describe [opt : (Option Int)]) : String
  (match opt
    [(Some v) (string-append ""Got: "" (int->string v))]
    [None ""Nothing""]))");
        Assert.Contains("switch", cs);
        Assert.Contains("Some", cs);
        Assert.Contains("None", cs);
    }

    [Fact]
    public void PreludeError_ErrorFunction_Available()
    {
        var cs = Compile(@"(define (make-err) : ErrorInfo (Error ""oops""))");
        Assert.Contains("ErrorInfo", cs);
    }

    [Fact]
    public void CollectionLiterals_UseImmutableTypes()
    {
        var cs = Compile(@"(define primes (list 2 3 5 7 11))");
        Assert.Contains("ImmutableList.Create", cs);
    }
}
