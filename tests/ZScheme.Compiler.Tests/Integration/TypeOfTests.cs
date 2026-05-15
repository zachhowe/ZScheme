using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

public class TypeOfTests
{
    private static CompilationResult CompileRaw(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        return compilation.Compile(source);
    }

    private static string Compile(string source)
    {
        var result = CompileRaw(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
        return ((CompilationResult.CSharpOutputResult)result).CsOutput;
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(TypeOfTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    [Fact]
    public void TypeOfPrimitive_EmitsTypeofInt()
    {
        var source = @"(module test)
(define (t) : System.Type (typeof Int))";
        var cs = Compile(source);
        Assert.Contains("typeof(int)", cs);
    }

    [Fact]
    public void TypeOfString_EmitsTypeofString()
    {
        var source = @"(module test)
(define (t) : System.Type (typeof String))";
        var cs = Compile(source);
        Assert.Contains("typeof(string)", cs);
    }

    [Fact]
    public void TypeOfUserRecord_EmitsTypeofRecord()
    {
        var source = @"(module test)
(define-record Point [x : Int] [y : Int])
(define (t) : System.Type (typeof Point))";
        var cs = Compile(source);
        Assert.Contains("typeof(", cs);
        Assert.Contains("Point", cs);
    }

    [Fact]
    public void TypeOfNullable_EmitsNullableTypeof()
    {
        var source = @"(module test)
(define (t) : System.Type (typeof Int?))";
        var cs = Compile(source);
        Assert.Contains("typeof(int?)", cs);
    }

    [Fact]
    public void TypeOfTupleType_EmitsValueTupleTypeof()
    {
        var source = @"(module test)
(define (t) : System.Type (typeof (Int * String)))";
        var cs = Compile(source);
        Assert.Contains("typeof((int, string))", cs);
    }

    [Fact]
    public void TypeOf_TypeIsSystemType()
    {
        // Round-trip through a binding annotated as System.Type to verify
        // type inference stamps ZNamedType("System.Type", []).
        var source = @"(module test)
(define (t) : System.Type
  (let [x : System.Type (typeof Int)]
    x))";
        var cs = Compile(source);
        Assert.Contains("typeof(int)", cs);
    }

    [Fact]
    public void TypeOf_ArityZero_ReportsDiagnostic()
    {
        var source = @"(module test)
(define (t) : System.Type (typeof))";
        var result = CompileRaw(source);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Diagnostics,
            d => d.Message.Contains("'typeof' requires exactly one type expression"));
    }

    [Fact]
    public void TypeOf_ArityTwo_ReportsDiagnostic()
    {
        var source = @"(module test)
(define (t) : System.Type (typeof Int String))";
        var result = CompileRaw(source);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Diagnostics,
            d => d.Message.Contains("'typeof' requires exactly one type expression"));
    }
}
