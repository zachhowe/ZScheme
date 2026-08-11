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

    // A method body goes through the same sequencing builder as every other body, so a nested
    // define needs no wrapper here. It was a single expression until this cycle, which made a
    // method the one place in the language where a definition had to be parenthesized into a
    // `let` first.
    private const string MethodBodyDefineSource =
        @"(module test)
(define-class Counter
  [start : Int]
  (define (Run [n : Int]) : Int
    (define (go [k : Int] [acc : Int]) : Int
      (if (= k 0) acc (go (- k 1) (+ acc k))))
    (go n 0)))
(define (compute) : Int (Counter/Run (new Counter 5) 10))";

    [Fact]
    public void DirectlyInAMethodBody_CSharp() =>
        Assert.Equal(55, CompileCSharpAndRunInt(MethodBodyDefineSource));

    [Fact]
    public void DirectlyInAMethodBody_Il() =>
        Assert.Equal(55, CompileIlAndRunInt(MethodBodyDefineSource));

    [Fact]
    public void MultiExpressionMethodBody_KeepsEveryForm()
    {
        // The forms after the first used to be dropped outright — later reported, but never
        // run. Sequencing them means the earlier ones take effect and the last one is the
        // method's value.
        const string source =
            @"(module test)
(define-class Counter
  [start : Int]
  (define (Run [n : Int]) : Int
    (let ([a (+ n 1)]) a)
    (let ([b (+ n 2)]) b)
    (+ n 3)))
(define (compute) : Int (Counter/Run (new Counter 5) 10))";

        Assert.Equal(13, CompileCSharpAndRunInt(source));
        Assert.Equal(13, CompileIlAndRunInt(source));
    }

    // A field that cannot change after construction is captured by value: the site reads it
    // through `this` like any other bare name in the method, and the lifted function takes it
    // as a leading parameter. `start` is 5 and the loop adds it 4 times.
    private const string FieldReadingLoopSource =
        @"(module test)
(define-class Counter
  [start : Int]
  (define (Run [n : Int]) : Int
    (define (go [k : Int] [acc : Int]) : Int
      (if (= k 0) acc (go (- k 1) (+ acc start))))
    (go n 0)))
(define (compute) : Int (Counter/Run (new Counter 5) 4))";

    [Fact]
    public void InsideAClassMethod_ReadingAField_CSharp() =>
        Assert.Equal(20, CompileCSharpAndRunInt(FieldReadingLoopSource));

    [Fact]
    public void InsideAClassMethod_ReadingAField_Il() =>
        Assert.Equal(20, CompileIlAndRunInt(FieldReadingLoopSource));

    [Fact]
    public void InsideAClassMethod_ReadingAField_CapturesItRatherThanReachingThroughThis()
    {
        // The field arrives as a leading parameter and the site supplies it — which is what
        // keeps the lifted function an ordinary static, needing no emitter change at all. It
        // also means one read per call rather than one per iteration.
        var cs = CompileCSharp(FieldReadingLoopSource);

        Assert.Contains("__letrec_test_0_go(int start, int k, int acc)", cs);
        Assert.Contains("__letrec_test_0_go(this.Start, n, 0)", cs);
        Assert.Contains("while (true)", cs);
    }

    // A `#:mutable` field cannot be captured by value, so the group is hosted on the class as a
    // private method and reads it through `this` on every iteration. Writing it from the loop
    // works for the same reason: `set!`'s receiver is the `this` the method already has. The
    // loop below counts down, doubling the field each step: 1 -> 2 -> 4 -> 8.
    private const string MutableFieldLoopSource =
        @"(module test)
(define-class Counter
  [state : Int #:mutable]
  (define (Run [n : Int]) : Int
    (define (go [k : Int]) : Int
      (if (= k 0) state (begin (set! state (* state 2)) (go (- k 1)))))
    (go n)))
(define (compute) : Int (Counter/Run (new Counter 1) 3))";

    [Fact]
    public void InsideAClassMethod_UsingAMutableField_CSharp() =>
        Assert.Equal(8, CompileCSharpAndRunInt(MutableFieldLoopSource));

    [Fact]
    public void InsideAClassMethod_UsingAMutableField_Il() =>
        Assert.Equal(8, CompileIlAndRunInt(MutableFieldLoopSource));

    [Fact]
    public void InsideAClassMethod_UsingAMutableField_EmitsAPrivateMethodThatLoops()
    {
        var cs = CompileCSharp(MutableFieldLoopSource);

        Assert.Contains("private int __letrec_test_0_go(int k)", cs);
        Assert.Contains("return this.__letrec_test_0_go(n);", cs);
        Assert.Contains("this.State = (this.State * 2);", cs);
        Assert.Contains("while (true)", cs);
    }

    // A sibling method call from inside the loop: a bare `(Twice i)` resolves to `this.Twice`
    // in a method body, which is exactly what the helper is.
    private const string SiblingCallLoopSource =
        @"(module test)
(define-class Counter
  [start : Int]
  (define (Twice [n : Int]) : Int (* n 2))
  (define (Run [n : Int]) : Int
    (define (go [k : Int] [acc : Int]) : Int
      (if (= k 0) acc (go (- k 1) (+ acc (Twice k)))))
    (go n 0)))
(define (compute) : Int (Counter/Run (new Counter 0) 4))";

    [Fact]
    public void InsideAClassMethod_CallingASiblingMethod_CSharp() =>
        Assert.Equal(20, CompileCSharpAndRunInt(SiblingCallLoopSource));

    [Fact]
    public void InsideAClassMethod_CallingASiblingMethod_Il() =>
        Assert.Equal(20, CompileIlAndRunInt(SiblingCallLoopSource));

    // An `#:open` class: the helper is private and non-virtual, so it still loops even though
    // the methods the source wrote are virtual and deliberately do not.
    private const string OpenClassMutableLoopSource =
        @"(module test)
(define-class #:open Counter
  [state : Int #:mutable]
  (define (Run [n : Int]) : Int
    (define (go [k : Int]) : Int
      (if (= k 0) state (begin (set! state (+ state 1)) (go (- k 1)))))
    (go n)))
(define (compute) : Int (Counter/Run (new Counter 0) 1000000))";

    [Fact]
    public void InsideAnOpenClassMethod_RunsInConstantStack_CSharp() =>
        Assert.Equal(1000000, CompileCSharpAndRunInt(OpenClassMutableLoopSource));

    [Fact]
    public void InsideAnOpenClassMethod_RunsInConstantStack_Il() =>
        Assert.Equal(1000000, CompileIlAndRunInt(OpenClassMutableLoopSource));

    [Fact]
    public void InsideAnOpenClassMethod_HelperIsPrivateAndNotVirtual()
    {
        var cs = CompileCSharp(OpenClassMutableLoopSource);

        Assert.Contains("public virtual int Run(int n)", cs);
        Assert.Contains("private int __letrec_test_0_go(int k)", cs);
        Assert.DoesNotContain("virtual int __letrec_test_0_go", cs);
    }

    // Every field of a class lifted from an `(object ...)` stands for a captured local, so it
    // is immutable by construction — which makes this the shape that benefits most.
    private const string ObjectCaptureLoopSource =
        @"(module test)
(define-interface Summer
  (Sum [n : Int] : Int))

(define (make-summer [step : Int]) : Summer
  (object Summer
    (define (Sum [n : Int]) : Int
      (define (go [k : Int] [acc : Int]) : Int
        (if (= k 0) acc (go (- k 1) (+ acc step))))
      (go n 0))))

(define (compute) : Int (Summer/Sum (make-summer 3) 4))";

    [Fact]
    public void InsideAnObjectMethod_ReadingACapture_CSharp() =>
        Assert.Equal(12, CompileCSharpAndRunInt(ObjectCaptureLoopSource));

    [Fact]
    public void InsideAnObjectMethod_ReadingACapture_Il() =>
        Assert.Equal(12, CompileIlAndRunInt(ObjectCaptureLoopSource));

    // The point of the whole exercise: a loop helper written inside a method, reading the
    // instance's state, running in constant stack. A million iterations overflow if the
    // captured field stopped the group becoming a loop — and the captured parameter is
    // reassigned on every back-edge, so a mis-ordered jump shows up as a wrong total rather
    // than a crash.
    private const string DeepFieldReadingLoopSource =
        @"(module test)
(define-class Counter
  [step : Int]
  (define (Run [n : Int]) : Int
    (define (go [i : Int] [acc : Int]) : Int
      (if (> i n) acc (go (+ i 1) (+ acc step))))
    (go 1 0)))
(define (compute) : Int (Counter/Run (new Counter 1) 1000000))";

    [Fact]
    public void FieldReadingLoopInAMethod_RunsInConstantStack_CSharp() =>
        Assert.Equal(1000000, CompileCSharpAndRunInt(DeepFieldReadingLoopSource));

    [Fact]
    public void FieldReadingLoopInAMethod_RunsInConstantStack_Il() =>
        Assert.Equal(1000000, CompileIlAndRunInt(DeepFieldReadingLoopSource));
}
