using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZScheme.Compiler.Pipeline;
using Compilation = ZScheme.Compiler.Pipeline.Compilation;

namespace ZScheme.Compiler.Tests.Integration;

// A class implementing a ZScheme interface that *inherits* methods from a base interface must
// mark those inherited methods as interface implementations too, or the CLR refuses to load the
// type (`TypeLoadException: Method 'Go' ... does not have an implementation`) the moment anything
// touches it — including construction, which is nowhere near the interface declaration.
//
// The IL backend decides `NewSlot | Final` from a set of interface method names, and that set was
// only walked transitively for CLR interfaces; a ZScheme-defined interface contributed its own
// methods and nothing from its base list. csc matches implicit implementations against the whole
// interface set transitively, so the C# backend was always right — which made this a silent
// backend divergence, and why every test here runs both backends and asserts they agree.
//
// The entry point is a zero-arg `compute` returning Int.
//
// The second half of the file covers the type checker's side of the same shape: it too walked a
// single declared edge, so an interface's inherited members were invisible (`IDerived-Go` on an
// `IDerived` was an undefined variable) and every widening past one edge was a type mismatch —
// on programs the CLR would have accepted, since the declarations were always emitted right.
public class InheritedInterfaceMethodTests
{
    // One inherited edge: the minimal shape. `Extra` is declared on `IDerived` and was always
    // marked correctly; `Go` is inherited from `IBase` and was emitted as a plain instance method.
    private const string OneEdge = """
        (module test)
        (define-interface IBase (Go [] : Int))
        (define-interface IDerived : IBase (Extra [] : Int))
        (define-class Impl : IDerived
          (define (Go) : Int 2)
          (define (Extra) : Int 40))
        (define (compute) : Int (+ (IDerived-Extra (Impl)) (Impl-Go (Impl))))
        """;

    [Fact]
    public void ClassImplementsMethodInheritedFromBaseInterface() => AssertBackendsAgree(OneEdge, 42);

    // The walk has to be transitive, not one edge deeper: `A` is two inherited edges away from the
    // interface `Impl` actually declares.
    private const string ThreeDeep = """
        (module test)
        (define-interface IA (A [] : Int))
        (define-interface IB : IA (B [] : Int))
        (define-interface IC : IB (C [] : Int))
        (define-class Impl : IC
          (define (A) : Int 1)
          (define (B) : Int 20)
          (define (C) : Int 300))
        (define (compute) : Int
          (let ([i : Impl (Impl)])
            (+ (Impl-A i) (+ (Impl-B i) (IC-C i)))))
        """;

    [Fact]
    public void ClassImplementsMethodsInheritedTransitively() => AssertBackendsAgree(ThreeDeep, 321);

    // A diamond: both base interfaces have to be walked, not just the first.
    private const string MultipleBases = """
        (module test)
        (define-interface ILeft (L [] : Int))
        (define-interface IRight (R [] : Int))
        (define-interface IBoth : ILeft IRight (Both [] : Int))
        (define-class Impl : IBoth
          (define (L) : Int 5)
          (define (R) : Int 60)
          (define (Both) : Int 700))
        (define (compute) : Int
          (let ([i : Impl (Impl)])
            (+ (Impl-L i) (+ (Impl-R i) (IBoth-Both i)))))
        """;

    [Fact]
    public void ClassImplementsMethodsInheritedFromEveryBaseInterface() =>
        AssertBackendsAgree(MultipleBases, 765);

    // `EmitClass` collects from two positions: the name directly after `:` (which the parser puts
    // in BaseClassName, so every case above goes through that branch) and the rest, which land in
    // InterfaceNames. Both feed the same set, so put the inheriting interface in the second slot.
    private const string SecondInterfacePosition = """
        (module test)
        (define-interface IFirst (First [] : Int))
        (define-interface IBase (Go [] : Int))
        (define-interface IDerived : IBase (Extra [] : Int))
        (define-class Impl : IFirst IDerived
          (define (First) : Int 100)
          (define (Go) : Int 2)
          (define (Extra) : Int 40))
        (define (compute) : Int
          (let ([i : Impl (Impl)])
            (+ (IFirst-First i) (+ (IDerived-Extra i) (Impl-Go i)))))
        """;

    [Fact]
    public void ClassImplementsMethodInheritedByANonFirstInterface() =>
        AssertBackendsAgree(SecondInterfacePosition, 142);

    // ─── The type checker's half: subtyping and accessors past one declared edge ───

    // The reduced repro. `Impl` declares `IDerived`, which inherits `IBase`, so passing an `Impl`
    // where an `IBase` is wanted is legal — Unifier.IsZSchemeSubtype compared against the
    // directly-declared list and stopped, reporting "Type mismatch: 'IBase' vs 'Impl'".
    private const string ClassSatisfiesBaseInterface = """
        (module test)
        (define-interface IBase (Go [] : Int))
        (define-interface IDerived : IBase (Extra [] : Int))
        (define-class Impl : IDerived
          (define (Go) : Int 2)
          (define (Extra) : Int 40))
        (define (via-base [t : IBase]) : Int (IBase-Go t))
        (define (compute) : Int (+ (via-base (Impl)) (IDerived-Extra (Impl))))
        """;

    [Fact]
    public void ClassIsASubtypeOfItsInterfacesBaseInterface() =>
        AssertBackendsAgree(ClassSatisfiesBaseInterface, 42);

    // An interface widened to one it inherits — no class involved, so the walk has to start from
    // an interface as readily as from a class.
    private const string InterfaceWidensToItsBase = """
        (module test)
        (define-interface IBase (Go [] : Int))
        (define-interface IDerived : IBase (Extra [] : Int))
        (define-class Impl : IDerived
          (define (Go) : Int 2)
          (define (Extra) : Int 40))
        (define (via-base [t : IBase]) : Int (IBase-Go t))
        (define (widen [d : IDerived]) : Int (+ (via-base d) (IDerived-Extra d)))
        (define (compute) : Int (widen (Impl)))
        """;

    [Fact]
    public void InterfaceIsASubtypeOfTheInterfaceItInherits() =>
        AssertBackendsAgree(InterfaceWidensToItsBase, 42);

    // An inherited method is a member of the inheriting interface too, so it gets an accessor
    // under that name: `IDerived-Go` was "Undefined variable" because InferInterfaceDecl
    // registered accessors for the declared methods only.
    private const string InheritedMethodAccessor = """
        (module test)
        (define-interface IA (A [] : Int))
        (define-interface IB : IA (B [] : Int))
        (define-interface IC : IB (C [] : Int))
        (define-class Impl : IC
          (define (A) : Int 1)
          (define (B) : Int 20)
          (define (C) : Int 300))
        (define (compute) : Int
          (let ([i : IC (Impl)])
            (+ (IC-A i) (+ (IC-B i) (IC-C i)))))
        """;

    [Fact]
    public void AccessorExistsForAMethodAnInterfaceInheritsTransitively() =>
        AssertBackendsAgree(InheritedMethodAccessor, 321);

    // A method an interface redeclares keeps its own signature: the base's must not overwrite the
    // accessor already registered for it.
    private const string RedeclaredMethod = """
        (module test)
        (define-interface IBase (Go [] : Int))
        (define-interface IDerived : IBase (Go [] : Int))
        (define-class Impl : IDerived (define (Go) : Int 42))
        (define (compute) : Int (IDerived-Go (Impl)))
        """;

    [Fact]
    public void RedeclaringAnInheritedMethodKeepsOneAccessor() =>
        AssertBackendsAgree(RedeclaredMethod, 42);

    // A subclass has its base class's interfaces, which is the case the walk reaches through the
    // base *class* edge rather than a base interface. `#:open` + interface was the combination
    // that could not be extended at all.
    private const string SubclassInheritsInterfaces = """
        (module test)
        (define-interface IBase (Go [] : Int))
        (define-class #:open Base : IBase (define (Go) : Int 42))
        (define-class Sub : Base)
        (define (via-base [t : IBase]) : Int (IBase-Go t))
        (define (compute) : Int (via-base (Sub)))
        """;

    [Fact]
    public void SubclassIsASubtypeOfItsBaseClassesInterfaces() =>
        AssertBackendsAgree(SubclassInheritsInterfaces, 42);

    // ─── Dual-backend compile/run harness (mirrors Integration/TypeNameCasingTests) ───

    // Namespace doubles as the emitted assembly name, so it has to be unique per test: every
    // source here declares a type called `Impl`, and two assemblies sharing an identity would let
    // one test resolve the other's `Impl`.
    private static CompilerOptions Options(OutputMode mode)
    {
        return new CompilerOptions
        {
            OutputMode = mode,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            Namespace = "ZSchemeInheritedIface" + Guid.NewGuid().ToString("N"),
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
            "ZSchemeInheritedIfaceMethod" + Guid.NewGuid().ToString("N"),
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
