using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Generics;

public class GenericEmitterTests
{
    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            ModuleSearchPaths = [GetZUnitPath()],
            DisablePrelude = true
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Diagnostics));
        var csResult = (CompilationResult.CSharpOutputResult)result;
        return csResult.CsOutput;
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(GenericEmitterTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static string GetZUnitPath()
    {
        var dir = Path.GetDirectoryName(typeof(GenericEmitterTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "zunit", "src");
    }

    // --- Constraint emission tests ---

    [Fact]
    public void EmitFunction_WithStructConstraint()
    {
        var cs = Compile("(module test)\n(define (f [x : ^a]) : ^a :where (^a struct) x)");
        Assert.Contains("where T0 : struct", cs);
    }

    [Fact]
    public void EmitFunction_WithClassConstraint()
    {
        var cs = Compile("(module test)\n(define (f [x : ^a]) : ^a :where (^a class) x)");
        Assert.Contains("where T0 : class", cs);
    }

    [Fact]
    public void EmitFunction_WithNotNullConstraint()
    {
        var cs = Compile("(module test)\n(define (f [x : ^a]) : ^a :where (^a notnull) x)");
        Assert.Contains("where T0 : notnull", cs);
    }

    [Fact]
    public void EmitFunction_WithNewConstraint()
    {
        var cs = Compile("(module test)\n(define (f [x : ^a]) : ^a :where (^a new) x)");
        Assert.Contains("where T0 : new()", cs);
    }

    [Fact]
    public void EmitFunction_WithUnmanagedConstraint()
    {
        var cs = Compile("(module test)\n(define (f [x : ^a]) : ^a :where (^a unmanaged) x)");
        Assert.Contains("where T0 : unmanaged", cs);
    }

    [Fact]
    public void EmitFunction_WithDefaultConstraint_IsOmittedFromCSharp()
    {
        // C# CS8823: `default` constraint is only valid on override / explicit-interface
        // methods. Since absence of constraints already matches its semantics, we accept
        // the ZScheme-level `default` keyword but emit no C# where clause for it.
        var cs = Compile("(module test)\n(define (f [x : ^a]) : ^a :where (^a default) x)");
        Assert.DoesNotContain("where T0 : default", cs);
        Assert.DoesNotContain("where T0", cs);
    }

    [Fact]
    public void EmitFunction_WithClassAndNewConstraint()
    {
        var cs = Compile("(module test)\n(define (f [x : ^a]) : ^a :where (^a class new) x)");
        Assert.Contains("where T0 : class, new()", cs);
    }

    [Fact]
    public void EmitFunction_MultipleTypeParamConstraints()
    {
        var cs = Compile("(module test)\n(define (f [x : ^a] [y : ^b]) : ^a :where ((^a struct) (^b class)) x)");
        Assert.Contains("where T0 : struct", cs);
        Assert.Contains("where T1 : class", cs);
    }

    [Fact]
    public void EmitFunction_ConstraintOrdering_ClassBeforeNew()
    {
        var cs = Compile("(module test)\n(define (f [x : ^a]) : ^a :where (^a class new) x)");
        var whereIdx = cs.IndexOf("where T0 :");
        Assert.True(whereIdx >= 0);
        var clause = cs.Substring(whereIdx);
        var classIdx = clause.IndexOf("class");
        var newIdx = clause.IndexOf("new()");
        Assert.True(classIdx < newIdx, "class should come before new() in where clause");
    }

    [Fact]
    public void EmitFunction_NoConstraints_NoWhereClause()
    {
        var cs = Compile("(module test)\n(define (id [x : ^a]) : ^a x)");
        Assert.DoesNotContain("where", cs);
    }

    // --- Generic type emission tests ---

    [Fact]
    public void EmitGenericIdentityFunction()
    {
        var cs = Compile("(module test)\n(define (id [x : ^a]) : ^a x)");
        Assert.Contains("public static T0 Id<T0>(T0 x)", cs);
    }

    [Fact]
    public void EmitGenericRecord()
    {
        var cs = Compile("(record (Pair a b) [fst : a] [snd : b])");
        Assert.Contains("Pair<T0, T1>", cs);
    }

    [Fact]
    public void EmitGenericUnion()
    {
        var cs = Compile("(union (Maybe a) (Just [value : a]) (Nothing))");
        Assert.Contains("Maybe<T0>", cs);
    }

    [Fact]
    public void EmitGenericMultiTypeParams()
    {
        var cs = Compile("(module test)\n(define (const [x : ^a] [y : ^b]) : ^a x)");
        Assert.Contains("<T0, T1>", cs);
    }

    [Fact]
    public void EmitGenericHigherOrderFunction()
    {
        var cs = Compile("(module test)\n(define (apply [f : (Fn [^a] ^b)] [x : ^a]) : ^b (f x))");
        Assert.Contains("System.Func<T0, T1> f", cs);
    }

    [Fact]
    public void EmitUnion_WithStructConstraint_PropagatesConstraintToCaseRecords()
    {
        // Regression: derived case records inherited from a constrained
        // base union must repeat the where-clause, otherwise Roslyn
        // rejects the inheritance with CS0453 ("type T0 must be a
        // non-nullable value type to use as parameter T0 of base").
        var cs = Compile(
            "(module test)\n(union (FU ^a) :where (^a struct) (Both [a : ^a] [b : ^a]) (Neither))");
        Assert.Contains("public abstract record FU<T0> where T0 : struct;", cs);
        Assert.Contains("public sealed record Both<T0>(T0 A, T0 B) : FU<T0> where T0 : struct;", cs);
        Assert.Contains("public sealed record Neither<T0>() : FU<T0> where T0 : struct;", cs);
    }

    [Fact]
    public void EmitUnion_NoConstraint_NoWhereOnCaseRecords()
    {
        var cs = Compile("(module test)\n(union (FU ^a) (J [v : ^a]) (N))");
        Assert.Contains("public abstract record FU<T0>;", cs);
        Assert.Contains("public sealed record J<T0>(T0 V) : FU<T0>;", cs);
        Assert.DoesNotContain("where T0", cs);
    }

    [Fact]
    public void EmitMonomorphicFunction_HasNoTypeParams()
    {
        var cs = Compile("(module test)\n(define (add [x : Int] [y : Int]) : Int (+ x y))");
        Assert.DoesNotContain("<T", cs);
    }
}
