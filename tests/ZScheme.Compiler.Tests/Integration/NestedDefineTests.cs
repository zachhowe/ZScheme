using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

// End-to-end tests for a `define` nested inside a body. Every case compiles and RUNS through both
// backends and asserts they agree; the entry point is a zero-arg `compute` returning int.
//
// A nested define desugars to a `letrec` group, so LetrecTests already covers the lowering. What
// these add is the surface form: that the desugar picks the right groups, that scoping matches what
// the syntax implies, and that the properties a top-level helper had — constant-stack tail
// recursion, generic instantiation — survive being nested.
public class NestedDefineTests
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
            "ZSchemeNestedDefineExec",
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

    // --- The canonical case: a loop closing over the enclosing parameter ---

    // `n` is not threaded through `loop` as an argument; it is captured. This is the shape the
    // whole feature exists for. 1+2+...+10 = 55.
    private const string CapturingLoopSource =
        @"(module test)
(define (sum-to [n : Int]) : Int
  (define (loop [i : Int] [acc : Int]) : Int
    (if (> i n) acc (loop (+ i 1) (+ acc i))))
  (loop 1 0))
(define (compute) : Int (sum-to 10))";

    [Fact]
    public void CapturingLoop_CSharp() =>
        Assert.Equal(55, CompileCSharpAndRunInt(CapturingLoopSource));

    [Fact]
    public void CapturingLoop_Il() => Assert.Equal(55, CompileIlAndRunInt(CapturingLoopSource));

    // --- Mutual recursion between siblings ---

    // Both names have to be in scope in both bodies, which only works because a run of adjacent
    // defines becomes one group. 4 is even, so this returns 1.
    private const string MutualSource =
        @"(module test)
(define (classify [n : Int]) : Int
  (define (even? [k : Int]) : Bool (if (= k 0) #t (odd? (- k 1))))
  (define (odd? [k : Int]) : Bool (if (= k 0) #f (even? (- k 1))))
  (if (even? n) 1 0))
(define (compute) : Int (classify 4))";

    [Fact]
    public void MutuallyRecursiveSiblings_CSharp() =>
        Assert.Equal(1, CompileCSharpAndRunInt(MutualSource));

    [Fact]
    public void MutuallyRecursiveSiblings_Il() => Assert.Equal(1, CompileIlAndRunInt(MutualSource));

    // --- A value define alongside a function define ---

    // `base` is an ordinary local; `bump` captures it. bump(5) = 15.
    private const string ValueAndFunctionSource =
        @"(module test)
(define (mid [n : Int]) : Int
  (define base 10)
  (define (bump [k : Int]) : Int (+ k base))
  (bump n))
(define (compute) : Int (mid 5))";

    [Fact]
    public void ValueAndFunctionInOneGroup_CSharp() =>
        Assert.Equal(15, CompileCSharpAndRunInt(ValueAndFunctionSource));

    [Fact]
    public void ValueAndFunctionInOneGroup_Il() =>
        Assert.Equal(15, CompileIlAndRunInt(ValueAndFunctionSource));

    // --- A definition after an expression ---

    // Definitions do not have to lead the body. The leading `set!`-free expression is evaluated
    // and discarded, then the group scopes over the rest. 7 + 1 = 8.
    private const string MidBodySource =
        @"(module test)
(define (f [n : Int]) : Int
  (+ n 0)
  (define (g [k : Int]) : Int (+ k 1))
  (g n))
(define (compute) : Int (f 7))";

    [Fact]
    public void DefinitionAfterAnExpression_CSharp() =>
        Assert.Equal(8, CompileCSharpAndRunInt(MidBodySource));

    [Fact]
    public void DefinitionAfterAnExpression_Il() =>
        Assert.Equal(8, CompileIlAndRunInt(MidBodySource));

    // --- Two separate groups in one body ---

    // The second group can see the first, but not vice versa. a(2) = 3, b(3) = 6.
    private const string TwoGroupsSource =
        @"(module test)
(define (f [n : Int]) : Int
  (define (a [k : Int]) : Int (+ k 1))
  (+ n 0)
  (define (b [k : Int]) : Int (* (a k) 2))
  (b n))
(define (compute) : Int (f 2))";

    [Fact]
    public void TwoGroups_CSharp() => Assert.Equal(6, CompileCSharpAndRunInt(TwoGroupsSource));

    [Fact]
    public void TwoGroups_Il() => Assert.Equal(6, CompileIlAndRunInt(TwoGroupsSource));

    // --- Nested one level deeper ---

    // A define inside a nested define's own body. inner(3) = 3*2 = 6, plus the captured n=4 -> 10.
    private const string DoublyNestedSource =
        @"(module test)
(define (f [n : Int]) : Int
  (define (outer [k : Int]) : Int
    (define (inner [j : Int]) : Int (* j 2))
    (+ (inner k) n))
  (outer 3))
(define (compute) : Int (f 4))";

    [Fact]
    public void DoublyNested_CSharp() =>
        Assert.Equal(10, CompileCSharpAndRunInt(DoublyNestedSource));

    [Fact]
    public void DoublyNested_Il() => Assert.Equal(10, CompileIlAndRunInt(DoublyNestedSource));

    // --- Inside a let body ---

    // `let` bodies used to fold their forms by hand, which dropped a nested define silently.
    // g(5) with y=3 is 8.
    private const string InsideLetSource =
        @"(module test)
(define (f [n : Int]) : Int
  (let ([y 3])
    (define (g [k : Int]) : Int (+ k y))
    (g n)))
(define (compute) : Int (f 5))";

    [Fact]
    public void InsideALetBody_CSharp() => Assert.Equal(8, CompileCSharpAndRunInt(InsideLetSource));

    [Fact]
    public void InsideALetBody_Il() => Assert.Equal(8, CompileIlAndRunInt(InsideLetSource));

    // --- Shadowing a top-level name ---

    // The nested `helper` returns a different type from the top-level one, so resolving to the
    // wrong binding would not even type-check. 1 means the inner one won.
    private const string ShadowingSource =
        @"(module test)
(define (helper [n : Int]) : Int (* n 100))
(define (f [n : Int]) : Int
  (define (helper [k : Int]) : Bool (= k 0))
  (if (helper n) 1 2))
(define (compute) : Int (f 0))";

    [Fact]
    public void ShadowingATopLevelName_CSharp() =>
        Assert.Equal(1, CompileCSharpAndRunInt(ShadowingSource));

    [Fact]
    public void ShadowingATopLevelName_Il() => Assert.Equal(1, CompileIlAndRunInt(ShadowingSource));

    // --- Generic enclosing function ---

    // The shape the stdlib migration depends on: the nested helper restates the enclosing
    // function's `^a`, and `f` is a capture whose own type mentions it. go applies f twice:
    // 1 -> 6 -> 11.
    private const string GenericSource =
        @"(module test)
(define (twice [x : ^a] [f : (^a -> ^a)]) : ^a
  (define (go [n : Int] [acc : ^a]) : ^a
    (if (= n 0) acc (go (- n 1) (f acc))))
  (go 2 x))
(define (compute) : Int
  (twice 1 (lambda ([k : Int]) : Int (+ k 5))))";

    [Fact]
    public void GenericEnclosingFunction_CSharp() =>
        Assert.Equal(11, CompileCSharpAndRunInt(GenericSource));

    [Fact]
    public void GenericEnclosingFunction_Il() =>
        Assert.Equal(11, CompileIlAndRunInt(GenericSource));

    [Fact]
    public void GenericEnclosingFunction_LiftsToAGenericStatic()
    {
        // Pins the mechanism: erasing the type parameter to `object` would still run for this
        // case, so assert the lifted helper is genuinely generic and instantiated at the call.
        var cs = CompileCSharp(GenericSource);
        Assert.Contains("__letrec_test_0_go<", cs);
    }

    // A locally-polymorphic nested helper inside a NON-generic function, used at two
    // instantiations. (id 1) + (if (id #t) 2 3) = 1 + 2 = 3.
    private const string LocallyPolymorphicSource =
        @"(module test)
(define (compute) : Int
  (define (id [x : ^a]) : ^a x)
  (+ (id 1) (if (id #t) 2 3)))";

    [Fact]
    public void LocallyPolymorphicHelper_CSharp() =>
        Assert.Equal(3, CompileCSharpAndRunInt(LocallyPolymorphicSource));

    [Fact]
    public void LocallyPolymorphicHelper_Il() =>
        Assert.Equal(3, CompileIlAndRunInt(LocallyPolymorphicSource));

    // --- Tail call optimization ---

    // The load-bearing property for the stdlib migration: a nested tail-recursive loop still
    // becomes a real loop, so it runs in constant stack on both backends. A million iterations
    // would overflow otherwise — and the capture makes this stricter than the letrec case, since
    // the captured parameter is reassigned on every back-edge.
    private const string TcoSource =
        @"(module test)
(define (count-to [n : Int]) : Int
  (define (loop [i : Int] [acc : Int]) : Int
    (if (> i n) acc (loop (+ i 1) (+ acc 1))))
  (loop 1 0))
(define (compute) : Int (count-to 1000000))";

    [Fact]
    public void NestedTailRecursion_RunsInConstantStack_CSharp() =>
        Assert.Equal(1000000, CompileCSharpAndRunInt(TcoSource));

    [Fact]
    public void NestedTailRecursion_RunsInConstantStack_Il() =>
        Assert.Equal(1000000, CompileIlAndRunInt(TcoSource));

    [Fact]
    public void NestedTailRecursion_EmitsALoopNotRecursion()
    {
        var cs = CompileCSharp(TcoSource);
        Assert.Contains("while (true)", cs);
        Assert.Contains("continue;", cs);
    }

    // --- Inside a class method ---

    // A group in a method body is lifted to a module-level static, so it must not read `this`.
    // Passing the field in as an argument is the supported form. go(10, 5) = 60.
    private const string ClassMethodSource =
        @"(module test)
(define-class Counter
  [start : Int]
  (define (Run [n : Int]) : Int
    (let ([seed start])
      (define (go [k : Int] [acc : Int]) : Int
        (if (= k 0) acc (go (- k 1) (+ acc k))))
      (go n seed))))
(define (compute) : Int
  (Counter/Run (new Counter 5) 10))";

    [Fact]
    public void InsideAClassMethod_CSharp() =>
        Assert.Equal(60, CompileCSharpAndRunInt(ClassMethodSource));

    [Fact]
    public void InsideAClassMethod_Il() => Assert.Equal(60, CompileIlAndRunInt(ClassMethodSource));

    [Fact]
    public void DirectlyInAMethodBody_ReportsError()
    {
        // A method body is a single expression, so a nested define there needs a wrapper. Without
        // the diagnostic the trailing forms were dropped and the failure surfaced as a type error
        // on the method's return type.
        var result = CompileWith(
            @"(module test)
(define-class Counter
  [start : Int]
  (define (Run [n : Int]) : Int
    (define (go [k : Int]) : Int k)
    (go n)))
(define (compute) : Int (Counter/Run (new Counter 5) 10))",
            OutputMode.CSharp
        );

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("has more than one body expression")
        );
    }

    [Fact]
    public void InsideAClassMethod_ReadingAFieldDirectly_ReportsError()
    {
        // The lifted static has no instance, so the field has to be passed in. This is the
        // existing letrec restriction surfacing through the new syntax.
        var result = CompileWith(
            @"(module test)
(define-class Counter
  [start : Int]
  (define (Run [n : Int]) : Int
    (let ([seed 0])
      (define (go [k : Int] [acc : Int]) : Int
        (if (= k 0) (+ acc start) (go (- k 1) (+ acc k))))
      (go n seed))))
(define (compute) : Int (Counter/Run (new Counter 5) 10))",
            OutputMode.CSharp
        );

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("reads the field 'start'")
        );
    }
}
