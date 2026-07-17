using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZScheme.Compiler.Pipeline;
using Compilation = ZScheme.Compiler.Pipeline.Compilation;

namespace ZScheme.Compiler.Tests.Integration;

// End-to-end coverage for the ClosureConverter lowering pass (EnableClosureConversion = true):
// capturing lambdas are lifted to top-level static functions + IrNode.Closure nodes, which both
// backends must consume and execute in agreement. Each test compiles and RUNS the program through
// both the IL and C# backends and asserts they produce the same value — the invariant most at risk
// from re-deriving closure semantics. The entry point is a zero-arg `Compute` returning int.
public class ClosureConversionEndToEndTests
{
    private static CompilerOptions Options(OutputMode mode)
    {
        return new CompilerOptions
        {
            OutputMode = mode,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            EnableClosureConversion = true,
        };
    }

    private static int RunIl(string source)
    {
        var result = new Compilation(Options(OutputMode.Il)).Compile(source);
        Assert.True(
            result.Success,
            "IL compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var asm = Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes);
        return Invoke(asm);
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
            "ZSchemeClosureExec",
            [CSharpSyntaxTree.ParseText(cs)],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true,
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
        var il = RunIl(source);
        var cs = RunCSharp(source);
        Assert.Equal(expected, il);
        Assert.Equal(expected, cs);
    }

    [Fact]
    public void MakeAdder_CapturesParam()
    {
        AssertBackendsAgree(
            """
            (define (make-adder n)
              (lambda (x) (+ n x)))
            (define (Compute) : Int
              (let ([add5 (make-adder 5)])
                (add5 10)))
            """,
            15
        );
    }

    [Fact]
    public void Lambda_CapturesLetBoundVar()
    {
        AssertBackendsAgree(
            """
            (define (Compute) : Int
              (let ([a 3])
                (let ([f (lambda (x) (* x a))])
                  (f 7))))
            """,
            21
        );
    }

    [Fact]
    public void ReturnedClosure_InvokedMultipleTimes()
    {
        AssertBackendsAgree(
            """
            (define (counter-from start)
              (lambda (delta) (+ start delta)))
            (define (Compute) : Int
              (let ([c (counter-from 100)])
                (+ (c 1) (c 2))))
            """,
            203
        );
    }

    [Fact]
    public void Lambda_CapturesMultipleVars()
    {
        AssertBackendsAgree(
            """
            (define (Compute) : Int
              (let ([a 2])
                (let ([b 10])
                  (let ([f (lambda (x) (+ (* a x) b))])
                    (f 4)))))
            """,
            18
        );
    }

    [Fact]
    public void NestedCapturingLambdas()
    {
        // The inner lambda captures both `a` (outer let) and `y` (outer lambda's param).
        AssertBackendsAgree(
            """
            (define (Compute) : Int
              (let ([a 1000])
                (let ([outer (lambda (y) (lambda (z) (+ (+ a y) z)))])
                  (let ([inner (outer 30)])
                    (inner 7)))))
            """,
            1037
        );
    }

    [Fact]
    public void CaptureLessLambda_StillWorks()
    {
        // No captures -> left as a bare FuncDef; must still compile and run on both backends.
        AssertBackendsAgree(
            """
            (define (Compute) : Int
              (let ([f (lambda (x) (* x x))])
                (f 9)))
            """,
            81
        );
    }
}
