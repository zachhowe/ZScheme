using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZScheme.Compiler.Pipeline;
using Compilation = ZScheme.Compiler.Pipeline.Compilation;

namespace ZScheme.Compiler.Tests.Integration;

// A bare `(Derived args...)` call on a class with a base class used to drop every inherited
// field. Three places describe such a constructor and one disagreed: the type checker built its
// type as `inheritedFields ++ ownFields`, both backends emitted
// `Derived(baseFields…, ownFields…) : base(baseFields…)`, and IrLowering registered it under the
// class's *own* field names alone. The call site zips the argument list against that list, and
// Zip truncates to the shorter side, so the inherited arguments vanished without an arity error:
// with no own fields the IL backend quietly returned an object with every inherited field
// defaulted, and the C# backend emitted `new Derived()` for csc to reject.
//
// The entry point is a zero-arg `compute` returning Int, and every case runs on both backends —
// the original failure was a divergence, wrong output on one and a hard compile error on the
// other.
public class DerivedClassConstructorTests
{
    // No own fields, so the whole argument list was discarded. This is the shape that returned a
    // wrong answer rather than failing.
    private const string NoOwnFields = """
        (module test)
        (define-class #:open BaseThing
          [n : Int]
          (define (Value) : Int n))
        (define-class Derived : BaseThing
          (define (Total) : Int (+ n 10)))
        (define (compute) : Int (Derived-Total (Derived 1)))
        """;

    [Fact]
    public void BareCtorPassesInheritedFieldsWhenDerivedHasNoneOfItsOwn() =>
        AssertBackendsAgree(NoOwnFields, 11);

    // With own fields the truncation bound the arguments to the wrong parameters: argument 1 went
    // to the *own* field and the rest were dropped.
    private const string OwnFields = """
        (module test)
        (define-class #:open BaseThing
          [n : Int]
          (define (Value) : Int n))
        (define-class Mid : BaseThing
          [m : Int]
          (define (Sum) : Int (+ n m)))
        (define (compute) : Int (Mid-Sum (Mid 2 30)))
        """;

    [Fact]
    public void BareCtorOrdersInheritedFieldsBeforeOwnFields() => AssertBackendsAgree(OwnFields, 32);

    // The walk has to be transitive: `Leaf`'s constructor takes `n` from two edges up.
    private const string ThreeDeep = """
        (module test)
        (define-class #:open Base
          [n : Int])
        (define-class #:open Mid : Base
          [m : Int])
        (define-class Leaf : Mid
          [k : Int]
          (define (All) : Int (+ (+ n m) k)))
        (define (compute) : Int (Leaf-All (Leaf 4 50 600)))
        """;

    [Fact]
    public void BareCtorWalksTheWholeBaseChain() => AssertBackendsAgree(ThreeDeep, 654);

    // `(new Derived args...)` lowers through the same RecordNew path but matches positionally on
    // the registered field count, and was correct throughout. It has to stay that way — it is the
    // workaround the issue documented.
    private const string ClrNewForm = """
        (module test)
        (define-class #:open BaseThing
          [n : Int])
        (define-class Derived : BaseThing
          [m : Int]
          (define (Total) : Int (+ n m)))
        (define (compute) : Int (Derived-Total (new Derived 7 70)))
        """;

    [Fact]
    public void ClrNewFormStillPassesEveryField() => AssertBackendsAgree(ClrNewForm, 77);

    // A class with no base class is unaffected: nothing is prepended.
    private const string NoBaseClass = """
        (module test)
        (define-class Plain
          [a : Int]
          [b : Int]
          (define (Sum) : Int (+ a b)))
        (define (compute) : Int (Plain-Sum (Plain 8 80)))
        """;

    [Fact]
    public void BareCtorIsUnchangedForAClassWithNoBase() => AssertBackendsAgree(NoBaseClass, 88);

    // ─── Dual-backend compile/run harness (mirrors Integration/InheritedInterfaceMethodTests) ───

    // Namespace doubles as the emitted assembly name, so it has to be unique per test: the sources
    // here reuse type names, and two assemblies sharing an identity would let one test resolve
    // another's types.
    private static CompilerOptions Options(OutputMode mode)
    {
        return new CompilerOptions
        {
            OutputMode = mode,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            Namespace = "ZSchemeDerivedCtor" + Guid.NewGuid().ToString("N"),
        };
    }

    private static int RunIl(string source)
    {
        var result = new Compilation(Options(OutputMode.Il)).Compile(source);
        Assert.True(
            result.Success,
            "IL compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return Invoke(Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes));
    }

    private static int RunCSharp(string source)
    {
        var result = new Compilation(Options(OutputMode.CSharp)).Compile(source);
        Assert.True(
            result.Success,
            "C# compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var cs = ((CompilationResult.CSharpOutputResult)result).CsOutput;

        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        Assert.False(string.IsNullOrEmpty(tpa), "TRUSTED_PLATFORM_ASSEMBLIES unavailable");
        var references = tpa!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "ZSchemeDerivedCtorCs" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(cs)],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(
            emit.Success,
            "Roslyn emit failed:\n"
                + string.Join(
                    "\n",
                    emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                )
                + "\n--- Generated C# ---\n"
                + cs
        );
        return Invoke(Assembly.Load(ms.ToArray()));
    }

    private static int Invoke(Assembly asm)
    {
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        try
        {
            return (int)method.Invoke(null, null)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    private static void AssertBackendsAgree(string source, int expected)
    {
        Assert.Equal(expected, RunIl(source));
        Assert.Equal(expected, RunCSharp(source));
    }
}
