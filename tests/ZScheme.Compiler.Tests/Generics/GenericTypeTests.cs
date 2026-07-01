using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Generics;

/// <summary>
///     Tests verifying generic type support through the full compilation pipeline.
/// </summary>
public class GenericTypeTests
{
    private static string Compile(string source)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
                ModuleSearchPaths = [GetZUnitPath()],
                DisablePrelude = true,
            }
        );
        var result = compilation.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));
        var csResult = (CompilationResult.CSharpOutputResult)result;
        return csResult.CsOutput;
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(GenericTypeTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static string GetZUnitPath()
    {
        var dir = Path.GetDirectoryName(typeof(GenericTypeTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "zunit", "src");
    }

    [Fact]
    public void GenericIdentityFunction_CompilesAndEmits()
    {
        var cs = Compile("(module test)\n(define (id [x : ^a]) : ^a x)");
        Assert.Contains("T0 Id<T0>", cs);
        Assert.Contains("return x;", cs);
    }

    [Fact]
    public void GenericRecord_CompilesAndEmits()
    {
        var cs = Compile("(define-record (Box a) [value : a])");
        Assert.Contains("Box<T0>", cs);
        Assert.Contains("T0 Value", cs);
    }

    [Fact]
    public void GenericRecord_WithMultipleTypeParams()
    {
        var cs = Compile("(define-record (Pair a b) [fst : a] [snd : b])");
        Assert.Contains("Pair<T0, T1>", cs);
        Assert.Contains("T0 Fst", cs);
        Assert.Contains("T1 Snd", cs);
    }

    [Fact]
    public void GenericUnion_CompilesAndEmits()
    {
        var cs = Compile("(define-union (Maybe a) (Just [value : a]) (Nothing))");
        Assert.Contains("Maybe<T0>", cs);
        Assert.Contains("Just<T0>", cs);
        Assert.Contains("Nothing<T0>", cs);
    }

    [Fact]
    public void GenericMultiTypeParams_CompilesAndEmits()
    {
        var cs = Compile("(module test)\n(define (pair-first [x : ^a] [y : ^b]) : ^a x)");
        Assert.Contains("<T0, T1>", cs);
        Assert.Contains("T0 x", cs);
        Assert.Contains("T1 y", cs);
    }

    [Fact]
    public void GenericHigherOrderFunction_CompilesAndEmits()
    {
        var cs = Compile("(module test)\n(define (apply [f : (^a -> ^b)] [x : ^a]) : ^b (f x))");
        Assert.Contains("T1 Apply<T0, T1>(System.Func<T0, T1> f, T0 x)", cs);
    }

    [Fact]
    public void GenericWithCollectionType_CompilesAndEmits()
    {
        var cs = Compile(
            "(module test)\n(import stdlib/treelist)\n(define (wrap [x : ^a]) : (TreeList ^a) (treelist x))"
        );
        Assert.Contains("ImmutableList<T0> Wrap<T0>(T0 x)", cs);
    }

    [Fact]
    public void GenericFunction_WithConstraint_CompilesAndEmits()
    {
        var cs = Compile("(module test)\n(define (f [x : ^a]) : ^a :where (^a struct) x)");
        Assert.Contains("T0 F<T0>(T0 x)", cs);
        Assert.Contains("where T0 : struct", cs);
    }

    [Fact]
    public void GenericRecord_WithConstraint_CompilesAndEmits()
    {
        var cs = Compile("(define-record (Box ^a) :where (^a notnull) [value : ^a])");
        Assert.Contains("Box<T0>", cs);
        Assert.Contains("where T0 : notnull", cs);
    }

    [Fact]
    public void GenericUnion_WithConstraint_CompilesAndEmits()
    {
        var cs = Compile(
            "(define-union (Maybe ^a) :where (^a notnull) (Just [value : ^a]) (Nothing))"
        );
        Assert.Contains("Maybe<T0>", cs);
        Assert.Contains("where T0 : notnull", cs);
    }

    [Fact]
    public void MonomorphicFunction_HasNoTypeParams()
    {
        var cs = Compile("(module test)\n(define (add [x : Int] [y : Int]) : Int (+ x y))");
        Assert.DoesNotContain("<T", cs);
        Assert.DoesNotContain("where", cs);
    }
}
