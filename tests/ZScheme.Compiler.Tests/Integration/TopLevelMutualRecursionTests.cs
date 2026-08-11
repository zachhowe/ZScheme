using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

// End-to-end tests for forward references between sibling top-level `define`s. Every case
// compiles and RUNS through both backends; the entry point is a zero-arg `compute` returning int.
//
// What these pin is the type inferer's signature pre-pass: `InferProgram` registers every
// top-level function's signature before inferring any body, so a define may call a sibling that
// appears later in the file. Without it, the forward reference fails inference outright with
// "Undefined variable". NestedDefineTests covers the same shape one level down, where the
// `letrec` desugar is what pre-binds the group; the two paths are independent.
//
// The codegen half was already in place and is pinned separately by
// IlEmitterTests.EmitMutuallyRecursiveTopLevelFunctions, which builds the IR by hand.
public class TopLevelMutualRecursionTests
{
    private static CompilationResult CompileWith(string source, OutputMode mode)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = mode,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
            }
        );
        return compilation.Compile(source);
    }

    private static string CompileCSharp(string source)
    {
        var result = CompileWith(source, OutputMode.CSharp);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return ((CompilationResult.CSharpOutputResult)result).CsOutput;
    }

    private static int CompileIlAndRunInt(string source, string methodName = "Compute")
    {
        var result = CompileWith(source, OutputMode.Il);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var ilResult = (CompilationResult.IlOutputResult)result;
        return InvokeInt(Assembly.Load(ilResult.OutputBytes), methodName);
    }

    private static int CompileCSharpAndRunInt(string source, string methodName = "Compute")
    {
        return InvokeInt(RoslynCompile(CompileCSharp(source)), methodName);
    }

    private static Assembly RoslynCompile(string cs)
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        Assert.False(string.IsNullOrEmpty(tpa), "TRUSTED_PLATFORM_ASSEMBLIES unavailable");
        var references = tpa!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p =>
                (Microsoft.CodeAnalysis.MetadataReference)
                    Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)
            )
            .ToList();

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(cs);
        var options = new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
            Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release,
            allowUnsafe: true,
            nullableContextOptions: Microsoft.CodeAnalysis.NullableContextOptions.Enable
        );
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "ZSchemeTopLevelMutualRecExec",
            [tree],
            references,
            options
        );

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(
            emit.Success,
            "Roslyn emit failed:\n"
                + string.Join(
                    "\n",
                    emit.Diagnostics.Where(d =>
                        d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                    )
                )
        );
        return Assembly.Load(ms.ToArray());
    }

    private static MethodInfo FindMethod(Assembly asm, string methodName)
    {
        return asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
    }

    private static int InvokeInt(Assembly asm, string methodName)
    {
        try
        {
            return (int)FindMethod(asm, methodName).Invoke(null, null)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    // --- The canonical pair ---

    // `even?` calls `odd?`, which is declared after it. 10 is even, so this returns 1.
    private const string MutualSource =
        @"(module test)
(define (even? [k : Int]) : Bool (if (= k 0) #t (odd? (- k 1))))
(define (odd? [k : Int]) : Bool (if (= k 0) #f (even? (- k 1))))
(define (compute) : Int (if (even? 10) 1 0))";

    [Fact]
    public void MutuallyRecursiveTopLevelDefines_CSharp() =>
        Assert.Equal(1, CompileCSharpAndRunInt(MutualSource));

    [Fact]
    public void MutuallyRecursiveTopLevelDefines_Il() =>
        Assert.Equal(1, CompileIlAndRunInt(MutualSource));

    // --- A plain forward reference, no cycle ---

    // Not mutual, just out of order: `first` calls `second` before it is declared.
    private const string ForwardReferenceSource =
        @"(module test)
(define (first [n : Int]) : Int (+ 1 (second n)))
(define (second [n : Int]) : Int (* n 10))
(define (compute) : Int (first 4))";

    [Fact]
    public void ForwardReferenceToLaterDefine_CSharp() =>
        Assert.Equal(41, CompileCSharpAndRunInt(ForwardReferenceSource));

    [Fact]
    public void ForwardReferenceToLaterDefine_Il() =>
        Assert.Equal(41, CompileIlAndRunInt(ForwardReferenceSource));

    // --- Tail calls across the cycle stay constant-stack ---

    // A mutual cycle in tail position is not a *self* call, so TailCallLowering leaves it alone;
    // this depth is well inside the default stack and just pins that the cycle runs at all.
    private const string DeepMutualSource =
        @"(module test)
(define (ping [n : Int]) : Int (if (= n 0) 0 (pong (- n 1))))
(define (pong [n : Int]) : Int (if (= n 0) 1 (ping (- n 1))))
(define (compute) : Int (ping 10000))";

    [Fact]
    public void DeepMutualCycle_CSharp() =>
        Assert.Equal(0, CompileCSharpAndRunInt(DeepMutualSource));

    [Fact]
    public void DeepMutualCycle_Il() => Assert.Equal(0, CompileIlAndRunInt(DeepMutualSource));

    // --- Generic functions still generalize ---

    // The pre-pass binds a monomorphic placeholder for every top-level function so siblings can
    // see it. That placeholder has to come back out before generalization, or `pick` would be
    // inferred monomorphic and the second call site (at String) would fail to unify. This is the
    // regression guard for TypeEnv.RemoveBinding being dropped from InferDefine.
    private const string GenericSource =
        @"(module test)
(define (pick [a : ^a] [b : ^a]) : ^a a)
(define (str-len [s : String]) : Int (if (= s ""ab"") 2 0))
(define (compute) : Int (+ (str-len (pick ""ab"" ""cd"")) (pick 7 8)))";

    [Fact]
    public void GenericDefineStillGeneralizes_CSharp() =>
        Assert.Equal(9, CompileCSharpAndRunInt(GenericSource));

    [Fact]
    public void GenericDefineStillGeneralizes_Il() => Assert.Equal(9, CompileIlAndRunInt(GenericSource));

    // The flip side, and a deliberate limit rather than an oversight: a *forward* reference sees
    // the pre-pass's monomorphic placeholder, because the callee's body has not been inferred yet
    // and so it has nothing to generalize. Two call sites at different types therefore conflict.
    // This is exactly how a `letrec` group behaves for the same reason — polymorphic recursion is
    // undecidable — and matching it is the point. Declaring the generic function first (above)
    // works. If forward-referenced polymorphism is ever implemented, update this test on purpose.
    [Fact]
    public void ForwardReferenceToAGenericDefine_IsMonomorphic()
    {
        var result = CompileWith(
            @"(module test)
(define (compute) : Int (+ (str-len (pick ""ab"" ""cd"")) (pick 7 8)))
(define (pick [a : ^a] [b : ^a]) : ^a a)
(define (str-len [s : String]) : Int (if (= s ""ab"") 2 0))",
            OutputMode.CSharp
        );

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Diagnostics, d => d.Message.Contains("Type mismatch"));
    }

    // --- An unannotated forward reference ---

    // Neither param nor return type is written, so the pre-pass registers fresh variables and the
    // bodies unify them. The placeholder still has to be the *same* variable both times.
    private const string UnannotatedSource =
        @"(module test)
(define (outer n) (inner n))
(define (inner n) (* n 3))
(define (compute) : Int (outer 5))";

    [Fact]
    public void UnannotatedForwardReference_CSharp() =>
        Assert.Equal(15, CompileCSharpAndRunInt(UnannotatedSource));

    [Fact]
    public void UnannotatedForwardReference_Il() =>
        Assert.Equal(15, CompileIlAndRunInt(UnannotatedSource));

    // --- Duplicate top-level defines are rejected ---

    // A second top-level define does not shadow the first the way a nested one does: inference
    // finishes before any call is emitted, so every call site — including ones written between
    // the two — binds to the last definition. That is a trap rather than a feature, so it is an
    // error, and it also keeps two same-named functions from colliding on one emitted name.
    [Fact]
    public void DuplicateTopLevelDefine_IsAnError()
    {
        var result = CompileWith(
            @"(module test)
(define (twice [n : Int]) : Int (* n 2))
(define (twice [n : Int]) : Int (+ n n))
(define (compute) : Int (twice 3))",
            OutputMode.CSharp
        );

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("'twice' is already defined at the top level")
        );
    }

    // The same name at different *nesting* levels is still fine — a nested define shadowing a
    // top-level one is a supported, tested form (see NestedDefineTests).
    [Fact]
    public void NestedDefineShadowingATopLevelName_IsStillAllowed()
    {
        Assert.Equal(
            1,
            CompileIlAndRunInt(
                @"(module test)
(define (helper) : Int 0)
(define (compute) : Int
  (define (helper) : Int 1)
  (helper))"
            )
        );
    }
}
