using Xunit;
using ZScript.Compiler.Pipeline;

namespace ZScript.Compiler.Tests.Package;

/// <summary>
///     Tests that stdlib ZScript modules (option, result, error) compile successfully
///     via explicit imports with qualified module names.
/// </summary>
public class StdLibCompilationTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(StdLibCompilationTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScript.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
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
        var csResult = (CompilationResult.CSharpOutputResult)result;
        return csResult.CsOutput;
    }

    [Fact]
    public void Option_SomeNone_Available()
    {
        var cs = Compile(
            "(module test)\n(import stdlib/option)\n(define (f [x : Int]) : (Option Int) (if (> x 0) (Some x) None))");
        Assert.Contains("Some", cs);
        Assert.Contains("None", cs);
    }

    [Fact]
    public void Result_OkErr_Available()
    {
        var cs = Compile(
            "(module test)\n(import stdlib/result)\n(import stdlib/error)\n(define (f [x : Int]) : (Result Int ErrorInfo) (if (> x 0) (Ok x) (Err (Error \"bad\"))))");
        Assert.Contains("Ok", cs);
        Assert.Contains("Err", cs);
    }

    [Fact]
    public void Option_MatchWorks()
    {
        var cs = Compile(@"(module test)
(import stdlib/option)
(define (describe [opt : (Option Int)]) : String
  (match opt
    [(Some v) (string-append ""Got: "" (int->string v))]
    [None ""Nothing""]))");
        Assert.Contains("switch", cs);
        Assert.Contains("Some", cs);
        Assert.Contains("None", cs);
    }

    [Fact]
    public void Error_ErrorFunction_Available()
    {
        var cs = Compile(@"(module test)
(import stdlib/error)
(define (make-err) : ErrorInfo (Error ""oops""))");
        Assert.Contains("ErrorInfo", cs);
    }

    [Fact]
    public void CollectionLiterals_UseImmutableTypes()
    {
        var cs = Compile(@"(module test)
(define primes (list 2 3 5 7 11))");
        Assert.Contains("ImmutableList.Create", cs);
    }

    [Fact]
    public void List_Operations_Available()
    {
        var cs = Compile(@"(module test)
(import stdlib/list)
(define (sum-list [xs : (List Int)]) : Int
  (list/fold xs 0 (fn [acc x] (+ acc x))))");
        Assert.Contains("sum_list", cs);
        Assert.Contains("list__fold_loop", cs);
    }

    [Fact]
    public void Vector_Operations_Available()
    {
        var cs = Compile(@"(module test)
(import stdlib/vector)
(define (vec-len [xs : (Vector Int)]) : Int
  (vector/count xs))");
        Assert.Contains("vec_len", cs);
        Assert.Contains(".Length", cs);
    }

    [Fact]
    public void Map_Get_ReturnsOption()
    {
        var cs = Compile(@"(module test)
(import stdlib/map)
(import stdlib/option)
(define (lookup [m : (Map String Int)] [key : String]) : (Option Int)
  (map/get m key))");
        Assert.Contains("lookup", cs);
        Assert.Contains("Option", cs);
        Assert.Contains("Some", cs);
        Assert.Contains("None", cs);
    }
}
