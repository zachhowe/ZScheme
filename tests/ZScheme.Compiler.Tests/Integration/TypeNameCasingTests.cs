using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZScheme.Compiler.Pipeline;
using Compilation = ZScheme.Compiler.Pipeline.Compilation;

namespace ZScheme.Compiler.Tests.Integration;

// Type names are case-insensitive in ZScheme the same way function names are: `HttpResponse` and
// `http-response` are two *different* types that happen to sanitize to the same CLR identifier, and
// both must survive to codegen as distinct types with working constructors, accessors and patterns.
// Three things have to hold together for that, and each has its own section below:
//
//   1. EmitNameResolver gives the later collider a distinct emitted identifier (`_type`), keyed on
//      the raw source name, consistently for both backends and across module boundaries.
//   2. No *type position* in the grammar is gated on capitalisation, so a hyphenated name can be a
//      base class, an implemented interface, or a type argument wherever a PascalCase one can.
//   3. No *pattern position* is either: a bare arm naming a nullary union case matches that case
//      rather than binding a variable, whatever its spelling.
//
// Every runtime test executes the program through both backends and asserts they agree — the two
// emitters resolve type references through separate registries, so agreement is the real invariant.
// The entry point is a zero-arg `compute` returning Int.
public class TypeNameCasingTests
{
    private static CompilerOptions Options(OutputMode mode)
    {
        return new CompilerOptions
        {
            OutputMode = mode,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
        };
    }

    private static string CompileCSharp(string source)
    {
        var result = new Compilation(Options(OutputMode.CSharp)).Compile(source);
        Assert.True(
            result.Success,
            "C# compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return ((CompilationResult.CSharpOutputResult)result).CsOutput;
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
        var cs = CompileCSharp(source);

        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        Assert.False(string.IsNullOrEmpty(tpa), "TRUSTED_PLATFORM_ASSEMBLIES unavailable");
        var references = tpa!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "ZSchemeTypeNameCasing" + Guid.NewGuid().ToString("N"),
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

    // ---------------------------------------------------------------------------------------
    // 1. Colliding type names stay distinct types
    // ---------------------------------------------------------------------------------------

    // The reference case: `HttpResponse` and `http-response` both sanitize to `HttpResponse`, and
    // so do their two accessors. Each constructor and each accessor must reach its own struct.
    private const string CollidingStructs = """
        (module test)
        (define-struct HttpResponse [status-code : Int])
        (define-struct http-response [status-code : Int])
        (define (compute) : Int
          (+ (HttpResponse-status-code (HttpResponse 200))
             (http-response-status-code (http-response 4))))
        """;

    [Fact]
    public void CollidingStructs_EachAccessorReachesItsOwnStruct_BothBackends()
    {
        AssertBackendsAgree(CollidingStructs, 204);
    }

    [Fact]
    public void CollidingStructs_EmitTwoDistinctClrTypes()
    {
        var cs = CompileCSharp(CollidingStructs);
        Assert.Contains("record struct HttpResponse(int StatusCode)", cs);
        Assert.Contains("record struct HttpResponse_type(int StatusCode)", cs);
    }

    // A colliding type used in an annotation, not just at its construction site: the parameter
    // types must stay distinct or the two `compute` helpers would be one overload.
    [Fact]
    public void CollidingStructs_InParameterAnnotations_StayDistinct_BothBackends()
    {
        const string source = """
            (module test)
            (define-struct HttpResponse [status-code : Int])
            (define-struct http-response [status-code : Int])
            (define (ok [r : HttpResponse]) : Int (HttpResponse-status-code r))
            (define (bad [r : http-response]) : Int (http-response-status-code r))
            (define (compute) : Int (+ (ok (HttpResponse 200)) (bad (http-response 4))))
            """;
        AssertBackendsAgree(source, 204);
    }

    [Fact]
    public void CollidingRecords_CopyUpdateTargetsItsOwnRecord_BothBackends()
    {
        // `with` resolves the record type off the receiver, so a mixed-up rename would either
        // fail to compile or copy the wrong shape.
        const string source = """
            (module test)
            (define-record HttpResponse [status-code : Int])
            (define-record http-response [status-code : Int])
            (define (compute) : Int
              (+ (HttpResponse-status-code (with (HttpResponse 1) [status-code 200]))
                 (http-response-status-code (with (http-response 1) [status-code 4]))))
            """;
        AssertBackendsAgree(source, 204);
    }

    [Fact]
    public void CollidingClasses_MethodsResolveToTheirOwnClass_BothBackends()
    {
        const string source = """
            (module test)
            (define-class HttpResponse
              [status-code : Int]
              (define (Code) : Int status-code))
            (define-class http-response
              [status-code : Int]
              (define (Code) : Int (+ status-code 1000)))
            (define (compute) : Int
              (- (http-response-Code (http-response 1200)) (HttpResponse-Code (HttpResponse 200))))
            """;
        AssertBackendsAgree(source, 2000);
    }

    [Fact]
    public void CollidingInterfaces_EachImplementedByItsOwnClass_BothBackends()
    {
        const string source = """
            (module test)
            (define-interface IThing (Go [] : Int))
            (define-interface i-thing (Go [] : Int))
            (define-class Upper : IThing (define (Go) : Int 1))
            (define-class Lower : i-thing (define (Go) : Int 200))
            (define (via-upper [t : IThing]) : Int (IThing-Go t))
            (define (via-lower [t : i-thing]) : Int (i-thing-Go t))
            (define (compute) : Int (+ (via-upper (Upper)) (via-lower (Lower))))
            """;
        AssertBackendsAgree(source, 201);
    }

    [Fact]
    public void CollidingUnions_MatchResolvesEachCaseToItsOwnUnion_BothBackends()
    {
        // Both the union names and every case name collide pairwise, so each `match` has to be
        // annotated with the union its scrutinee actually belongs to.
        const string source = """
            (module test)
            (define-union Shape (Circle [r : Int]) (Square [s : Int]))
            (define-union shape (circle [r : Int]) (square [s : Int]))
            (define (upper [x : Shape]) : Int (match x [(Circle r) r] [(Square s) (+ s 10)]))
            (define (lower [x : shape]) : Int (match x [(circle r) (+ r 100)] [(square s) s]))
            (define (compute) : Int (+ (upper (Circle 1)) (lower (circle 2))))
            """;
        AssertBackendsAgree(source, 103);
    }

    [Fact]
    public void CollidingGenericRecords_StayDistinct_BothBackends()
    {
        const string source = """
            (module test)
            (define-record (Box ^a) [v : ^a])
            (define-record (box ^a) [v : ^a])
            (define (compute) : Int (+ (Box-v (Box 200)) (box-v (box 4))))
            """;
        AssertBackendsAgree(source, 204);
    }

    [Fact]
    public void ThreeWayTypeCollision_EachGetsItsOwnIdentifier_BothBackends()
    {
        // The suffix ladder is `_type`, then `_type2`; three spellings of one sanitized name is
        // what proves the allocator keeps counting rather than reusing the first free suffix.
        const string source = """
            (module test)
            (define-record HttpResponse [a : Int])
            (define-record http-response [b : Int])
            (define-record Http-Response [c : Int])
            (define (compute) : Int
              (+ (HttpResponse-a (HttpResponse 1))
                 (http-response-b (http-response 20))
                 (Http-Response-c (Http-Response 300))))
            """;
        var cs = CompileCSharp(source);
        Assert.Contains("record HttpResponse(int A)", cs);
        Assert.Contains("record HttpResponse_type(int B)", cs);
        Assert.Contains("record HttpResponse_type2(int C)", cs);
        AssertBackendsAgree(source, 321);
    }

    [Fact]
    public void NoCollision_LeavesTheHyphenatedTypeAtItsPlainSanitizedName()
    {
        // A hyphenated name is not renamed just for being hyphenated — only a genuine collision
        // moves a type, and the first claimant always keeps the base identifier.
        var cs = CompileCSharp(
            """
            (module test)
            (define-record http-response [status-code : Int])
            (define (compute) : Int (http-response-status-code (http-response 3)))
            """
        );
        Assert.Contains("record HttpResponse(int StatusCode)", cs);
        Assert.DoesNotContain("_type", cs);
    }

    // ---------------------------------------------------------------------------------------
    // 2. Type positions in the grammar are not gated on capitalisation
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void HyphenatedInterface_CanBeImplementedByAClass_BothBackends()
    {
        // The `: IFoo IBar` run used to stop at the first name that did not start with an
        // upper-case letter, so `i-thing` was dropped and then reported as a stray class member.
        const string source = """
            (module test)
            (define-interface i-thing (Go [] : Int))
            (define-class counter : i-thing
              [n : Int]
              (define (Go) : Int n))
            (define (run [t : i-thing]) : Int (i-thing-Go t))
            (define (compute) : Int (run (counter 42)))
            """;
        AssertBackendsAgree(source, 42);
    }

    [Fact]
    public void HyphenatedBaseClass_CanBeSubclassed_BothBackends()
    {
        const string source = """
            (module test)
            (define-class #:open base-thing
              (define (Value) : Int 5))
            (define-class derived-thing : base-thing
              [m : Int]
              (define (Total) : Int (+ (super/Value) m)))
            (define (compute) : Int (derived-thing-Total (derived-thing 3)))
            """;
        AssertBackendsAgree(source, 8);
    }

    [Fact]
    public void HyphenatedBaseInterface_CanBeExtendedByAnotherInterface()
    {
        // `define-interface`'s base run has the same shape as `define-class`'s and had the same
        // gate. Asserted on the emitted declaration: reaching an *inherited* interface method
        // through the derived interface is a separate, casing-independent gap.
        var cs = CompileCSharp(
            """
            (module test)
            (define-interface i-base (Go [] : Int))
            (define-interface i-derived : i-base (Extra [] : Int))
            (define (compute) : Int 0)
            """
        );
        Assert.Contains("interface IDerived : IBase", cs);
    }

    [Fact]
    public void HyphenatedInterface_InAnObjectExpression_BothBackends()
    {
        const string source = """
            (module test)
            (define-interface i-greeter (Greet [] : Int))
            (define (make) : i-greeter (object i-greeter (define (Greet) : Int 42)))
            (define (compute) : Int (i-greeter-Greet (make)))
            """;
        AssertBackendsAgree(source, 42);
    }

    [Fact]
    public void CollidingInterfaceNames_AreImplementedIndependently_BothBackends()
    {
        // The two halves together: a hyphenated name in a base/interface position *and* an
        // emitted-name collision with its PascalCase twin.
        const string source = """
            (module test)
            (define-interface IThing (Go [] : Int))
            (define-interface i-thing (Go [] : Int))
            (define-class Counter : IThing
              [n : Int]
              (define (Go) : Int n))
            (define-class counter : i-thing
              [n : Int]
              (define (Go) : Int (+ n 100)))
            (define (compute) : Int
              (+ (IThing-Go (Counter 1)) (i-thing-Go (counter 2))))
            """;
        AssertBackendsAgree(source, 103);
    }

    [Fact]
    public void WhereClause_OnAGenericClassWithNoBase_IsNotReadAsABaseClass()
    {
        // Regression guard for the base/interface run now that it no longer stops at the first
        // lower-case name: `:where` reaches the parser as a bare `:` followed by the symbol
        // `where`, which is shaped exactly like `: BaseClass`. The run has to decline that `:`
        // or `where` would be consumed as the base class and the constraints lost.
        var cs = CompileCSharp(
            """
            (module test)
            (define-class (holder ^a) :where (^a notnull)
              [v : ^a]
              (define (Get) : ^a v))
            (define (compute) : Int 0)
            """
        );
        Assert.Contains("where T0 : notnull", cs);
        Assert.DoesNotContain(": Where", cs);
    }

    [Fact]
    public void WhereClause_OnAGenericInterfaceWithNoBase_IsNotReadAsABaseInterface()
    {
        var cs = CompileCSharp(
            """
            (module test)
            (define-interface (i-holder ^a) :where (^a notnull) (Get [] : ^a))
            (define (compute) : Int 0)
            """
        );
        Assert.Contains("where T0 : notnull", cs);
        Assert.DoesNotContain(": Where", cs);
    }

    [Fact]
    public void WhereClause_AfterAHyphenatedBaseInterface_KeepsBoth()
    {
        // Both features in one header: the run consumes `i-base`, then stops at the `:` that
        // opens the constraint clause.
        var cs = CompileCSharp(
            """
            (module test)
            (define-interface i-base (Go [] : Int))
            (define-interface (i-holder ^a) : i-base :where (^a notnull) (Get [] : ^a))
            (define (compute) : Int 0)
            """
        );
        Assert.Contains(": IBase where T0 : notnull", cs);
    }

    // ---------------------------------------------------------------------------------------
    // 3. Pattern positions are not gated on capitalisation either
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void HyphenatedNullaryUnionCases_MatchAsConstructors_BothBackends()
    {
        // A bare atom arm parses as a variable binder unless it is upper-case, so these arms used
        // to compile to `x switch { var red => 3 }`: the first arm swallowed the rest and the
        // program silently returned the wrong value with no diagnostic.
        const string source = """
            (module test)
            (define-union traffic-light (go-now) (stop-now))
            (define (score [x : traffic-light]) : Int (match x [go-now 1] [stop-now 40]))
            (define (compute) : Int (+ (score go-now) (score stop-now)))
            """;
        AssertBackendsAgree(source, 41);
    }

    [Fact]
    public void HyphenatedNullaryUnionCase_EmitsATypeTestNotABinder()
    {
        var cs = CompileCSharp(
            """
            (module test)
            (define-union traffic-light (go-now) (stop-now))
            (define (compute) : Int (match go-now [go-now 1] [stop-now 40]))
            """
        );
        Assert.Contains("GoNow => 1", cs);
        Assert.Contains("StopNow => 40", cs);
        Assert.DoesNotContain("var goNow", cs);
    }

    [Fact]
    public void HyphenatedNullaryUnionCase_NestedInAConstructorPattern_BothBackends()
    {
        // The rewrite runs at every depth, not just on the arm's outermost pattern.
        const string source = """
            (module test)
            (define-union traffic-light (go-now) (stop-now))
            (define-union signal (at [light : traffic-light]))
            (define (score [s : signal]) : Int
              (match s [(at go-now) 1] [(at stop-now) 40]))
            (define (compute) : Int (+ (score (at go-now)) (score (at stop-now))))
            """;
        AssertBackendsAgree(source, 41);
    }

    [Fact]
    public void HyphenatedNullaryUnionCase_CountsTowardsExhaustiveness()
    {
        // The arms are real cases now, so a missing one is reported rather than hidden behind a
        // binder that made every match look exhaustive.
        var result = new Compilation(Options(OutputMode.CSharp)).Compile(
            """
            (module test)
            (define-union traffic-light (go-now) (stop-now))
            (define (score [x : traffic-light]) : Int (match x [go-now 1]))
            """
        );
        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("stop-now", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void BareArmThatIsNotAUnionCase_StillBindsAVariable_BothBackends()
    {
        // The rewrite is keyed on declared nullary case names, so an ordinary catch-all binder —
        // including one whose name merely resembles a case name — keeps binding.
        const string source = """
            (module test)
            (define-union traffic-light (go-now) (stop-now))
            (define (compute) : Int (match 40 [n (+ n 2)]))
            """;
        AssertBackendsAgree(source, 42);
    }

    [Fact]
    public void UnionMixingNullaryAndFieldCases_MatchesBothSpellings_BothBackends()
    {
        // Only *zero-field* cases are eligible for the bare-atom rewrite; a case with fields
        // still has to be written as a constructor pattern, and the two coexist in one match.
        const string source = """
            (module test)
            (define-union verbosity (off) (level [n : Int]))
            (define (score [v : verbosity]) : Int (match v [off 2] [(level n) n]))
            (define (compute) : Int (+ (score off) (score (level 40))))
            """;
        AssertBackendsAgree(source, 42);
    }

    [Fact]
    public void UpperCaseNullaryUnionCase_StillMatches_BothBackends()
    {
        // The pre-existing spelling rule is untouched: an upper-case bare atom is a constructor
        // pattern whether or not any union declares it.
        const string source = """
            (module test)
            (define-union Color (Red) (Green))
            (define (score [c : Color]) : Int (match c [Red 2] [Green 40]))
            (define (compute) : Int (+ (score Red) (score Green)))
            """;
        AssertBackendsAgree(source, 42);
    }

    // ---------------------------------------------------------------------------------------
    // 4. Across a module boundary
    // ---------------------------------------------------------------------------------------

    private static CompilationResult CompileWithModule(
        string moduleSource,
        string mainSource,
        OutputMode mode
    )
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_casing_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "types.zs"), moduleSource);
            var mainPath = Path.Combine(dir, "main.zs");
            File.WriteAllText(mainPath, mainSource);

            var options = Options(mode);
            options.ModuleSearchPaths.Add(dir);
            return new Compilation(options).Compile(mainSource, mainPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private const string CollidingTypesModule = """
        (module types)
        (export HttpResponse http-response traffic-light go-now stop-now)
        (define-struct HttpResponse [status-code : Int])
        (define-struct http-response [status-code : Int])
        (define-union traffic-light (go-now) (stop-now))
        """;

    [Fact]
    public void CollidingTypesFromAnImportedModule_StayDistinct_BothBackends()
    {
        // The importing module allocates emitted names for its dependency's types too, so both
        // sides have to agree on which one moved — a mismatch resolves an accessor to the wrong
        // record rather than failing loudly.
        const string main = """
            (module main)
            (import types)
            (define (compute) : Int
              (+ (HttpResponse-status-code (HttpResponse 200))
                 (http-response-status-code (http-response 4))))
            """;

        var il = CompileWithModule(CollidingTypesModule, main, OutputMode.Il);
        Assert.True(
            il.Success,
            "IL compilation failed:\n" + string.Join("\n", il.Diagnostics.Diagnostics)
        );
        Assert.Equal(204, Invoke(Assembly.Load(((CompilationResult.IlOutputResult)il).OutputBytes)));

        var cs = CompileWithModule(CollidingTypesModule, main, OutputMode.CSharp);
        Assert.True(
            cs.Success,
            "C# compilation failed:\n" + string.Join("\n", cs.Diagnostics.Diagnostics)
        );
        Assert.Contains("HttpResponse_type", ((CompilationResult.CSharpOutputResult)cs).CsOutput);
    }

    [Fact]
    public void ImportedHyphenatedNullaryUnionCase_MatchesAsAConstructor_Il()
    {
        // The nullary case names an importing module has to recognise arrive as IR, not AST, so
        // the pipeline feeds them in separately from the file's own declarations.
        const string main = """
            (module main)
            (import types)
            (define (score [x : traffic-light]) : Int (match x [go-now 2] [stop-now 40]))
            (define (compute) : Int (+ (score go-now) (score stop-now)))
            """;

        var il = CompileWithModule(CollidingTypesModule, main, OutputMode.Il);
        Assert.True(
            il.Success,
            "IL compilation failed:\n" + string.Join("\n", il.Diagnostics.Diagnostics)
        );
        Assert.Equal(42, Invoke(Assembly.Load(((CompilationResult.IlOutputResult)il).OutputBytes)));
    }
}
