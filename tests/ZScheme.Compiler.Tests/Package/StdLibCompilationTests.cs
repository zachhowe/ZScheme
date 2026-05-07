using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Package;

/// <summary>
///     Tests that stdlib ZScheme modules (option, result, error) compile successfully
///     via explicit imports with qualified module names.
/// </summary>
public class StdLibCompilationTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(StdLibCompilationTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
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
(import stdlib/treelist)
(define (make-primes) : (TreeList Int) (treelist 2 3 5 7 11))");
        Assert.Contains("MakePrimes", cs);
    }

    [Fact]
    public void List_Operations_Available()
    {
        var cs = Compile(@"(module test)
(import stdlib/list)
(define (sum-list [xs : (List Int)]) : Int
  (fold xs 0 (lambda (acc x) (+ acc x))))");
        Assert.Contains("SumList", cs);
    }

    [Fact]
    public void Array_Operations_Available()
    {
        var cs = Compile(@"(module test)
(import stdlib/array)
(define (arr-len [xs : (Array Int)]) : Int
  (array-length xs))");
        Assert.Contains("ArrLen", cs);
        Assert.Contains(".Length", cs);
    }

    [Fact]
    public void Map_Get_ReturnsOption()
    {
        var cs = Compile(@"(module test)
(import stdlib/map)
(import stdlib/option)
(define (lookup [m : (Map String Int)] [key : String]) : (Option Int)
  (get m key))");
        Assert.Contains("Lookup", cs);
        Assert.Contains("Option", cs);
        Assert.Contains("Some", cs);
        Assert.Contains("None", cs);
    }
}
