using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

// End-to-end tests for `letrec`. Every case compiles and RUNS through both backends and asserts
// they agree, because the two lower a recursive group along visibly different paths: the C#
// backend emits static methods plus native lambdas for the closure values, the IL backend emits
// static methods plus synthesized display classes. The entry point is a zero-arg `compute`
// returning int.
public class LetrecTests
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
            "ZSchemeLetrecExec",
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

    // --- Mutual recursion: the reason the form exists ---

    // even?/odd? cannot be expressed with let/let*: each needs the other in scope in its own
    // value. 10 is even, so this returns 1.
    private const string MutualRecursionSource =
        @"(module test)
(define (compute) : Int
  (letrec ([even? (lambda ([n : Int]) : Bool (if (= n 0) #t (odd? (- n 1))))]
           [odd? (lambda ([n : Int]) : Bool (if (= n 0) #f (even? (- n 1))))])
    (if (even? 10) 1 0)))";

    [Fact]
    public void MutualRecursion_CSharp() =>
        Assert.Equal(1, CompileCSharpAndRunInt(MutualRecursionSource));

    [Fact]
    public void MutualRecursion_Il() => Assert.Equal(1, CompileIlAndRunInt(MutualRecursionSource));

    // --- Self recursion ---

    // sum(5) = 5+4+3+2+1 = 15.
    private const string SelfRecursionSource =
        @"(module test)
(define (compute) : Int
  (letrec ([sum (lambda ([n : Int]) : Int (if (= n 0) 0 (+ n (sum (- n 1)))))])
    (sum 5)))";

    [Fact]
    public void SelfRecursion_CSharp() =>
        Assert.Equal(15, CompileCSharpAndRunInt(SelfRecursionSource));

    [Fact]
    public void SelfRecursion_Il() => Assert.Equal(15, CompileIlAndRunInt(SelfRecursionSource));

    // --- Capturing an enclosing local ---

    // `factor` is a local of `compute`, so the lifted function must take it as a leading
    // parameter and the closure must pass it in: 3*4 = 12.
    private const string CaptureSource =
        @"(module test)
(define (compute) : Int
  (let ([factor 3])
    (letrec ([scale (lambda ([n : Int]) : Int (if (= n 0) 0 (+ factor (scale (- n 1)))))])
      (scale 4))))";

    [Fact]
    public void CapturesEnclosingLocal_CSharp() =>
        Assert.Equal(12, CompileCSharpAndRunInt(CaptureSource));

    [Fact]
    public void CapturesEnclosingLocal_Il() => Assert.Equal(12, CompileIlAndRunInt(CaptureSource));

    // --- A letrec function used as a value ---

    // Passing the binding rather than calling it exercises the IrNode.Closure path (a native
    // lambda on C#, a display class on IL) instead of the direct-call path. step(4) = 4, applied
    // twice is still 4.
    private const string ValuePositionSource =
        @"(module test)
(define (apply-twice [g : (Int -> Int)] [n : Int]) : Int
  (g (g n)))
(define (compute) : Int
  (letrec ([step (lambda ([k : Int]) : Int (if (= k 0) 0 (+ 1 (step (- k 1)))))])
    (apply-twice step 4)))";

    [Fact]
    public void FunctionInValuePosition_CSharp() =>
        Assert.Equal(4, CompileCSharpAndRunInt(ValuePositionSource));

    [Fact]
    public void FunctionInValuePosition_Il() =>
        Assert.Equal(4, CompileIlAndRunInt(ValuePositionSource));

    // Two value-position uses of one member produce two IrNode.Closure nodes naming the same
    // lifted function. The IL backend names its display class after that function, so a type per
    // Closure node put two identically named nested types on one parent and the emitter refused to
    // write the metadata; the display class is now shared per enclosing type. `base` is captured so
    // the shared shape has a field to get wrong. step(k) = base + k, so with base = 10 this is
    // (10+2) + (10+3) = 25.
    private const string ValuePositionTwiceSource =
        @"(module test)
(define (apply-to [g : (Int -> Int)] [n : Int]) : Int
  (g n))
(define (from [base : Int]) : Int
  (letrec ([step (lambda ([k : Int]) : Int (if (= k 0) base (+ 1 (step (- k 1)))))])
    (+ (apply-to step 2) (apply-to step 3))))
(define (compute) : Int
  (from 10))";

    [Fact]
    public void FunctionInValuePositionTwice_CSharp() =>
        Assert.Equal(25, CompileCSharpAndRunInt(ValuePositionTwiceSource));

    [Fact]
    public void FunctionInValuePositionTwice_Il() =>
        Assert.Equal(25, CompileIlAndRunInt(ValuePositionTwiceSource));

    // The captured value differs per call, so a shared display *type* must still mean a fresh
    // display *instance* per construction site. from(10) = 25 and from(100) = 205.
    [Fact]
    public void FunctionInValuePositionTwice_CapturesPerInstance_Il()
    {
        var source = ValuePositionTwiceSource.Replace(
            "(from 10))",
            "(+ (from 10) (* 1000 (from 100))))"
        );
        Assert.Equal(205025, CompileIlAndRunInt(source));
    }

    // --- A mixed group ---

    // `base` is a plain value, `f` captures it, and `result` calls `f`. The site has to bind
    // `base` first, then f's closure, then `result` — even though `f` is written first.
    private const string MixedGroupSource =
        @"(module test)
(define (compute) : Int
  (letrec ([f (lambda ([n : Int]) : Int (+ n base))]
           [base 10]
           [result (f 5)])
    result))";

    [Fact]
    public void MixedGroup_CSharp() => Assert.Equal(15, CompileCSharpAndRunInt(MixedGroupSource));

    [Fact]
    public void MixedGroup_Il() => Assert.Equal(15, CompileIlAndRunInt(MixedGroupSource));

    // --- Nested groups ---

    // inner(3) = (outer 3) + (outer 2) + (outer 1) = 4+3+2 = 9.
    private const string NestedSource =
        @"(module test)
(define (compute) : Int
  (letrec ([outer (lambda ([k : Int]) : Int (+ k 1))])
    (letrec ([inner (lambda ([k : Int]) : Int
                      (if (= k 0) 0 (+ (outer k) (inner (- k 1)))))])
      (inner 3))))";

    [Fact]
    public void NestedGroups_CSharp() => Assert.Equal(9, CompileCSharpAndRunInt(NestedSource));

    [Fact]
    public void NestedGroups_Il() => Assert.Equal(9, CompileIlAndRunInt(NestedSource));

    // --- A group inside a lambda that itself captures ---

    // The letrec sits inside a lambda that captures `x`, so the lifted function's capture has to
    // survive being lifted twice. go(3) with x=9 is 3+9 = 12; applied twice, 12+9 = 21.
    private const string LetrecInsideCapturingLambdaSource =
        @"(module test)
(define (apply-twice [g : (Int -> Int)] [n : Int]) : Int
  (g (g n)))
(define (compute) : Int
  (let ([x 9])
    (apply-twice (lambda ([n : Int]) : Int
                   (letrec ([go (lambda ([k : Int]) : Int
                                  (if (= k 0) x (+ 1 (go (- k 1)))))])
                     (go n)))
                 3)))";

    [Fact]
    public void LetrecInsideCapturingLambda_CSharp() =>
        Assert.Equal(21, CompileCSharpAndRunInt(LetrecInsideCapturingLambdaSource));

    [Fact]
    public void LetrecInsideCapturingLambda_Il() =>
        Assert.Equal(21, CompileIlAndRunInt(LetrecInsideCapturingLambdaSource));

    // --- Module top level ---

    // A letrec bound at module level becomes a static field initialized in the static
    // constructor, so the lifted functions must be resolvable from there too. 5! = 120.
    private const string TopLevelSource =
        @"(module test)
(define fact-5
  (letrec ([fact (lambda ([k : Int]) : Int (if (= k 0) 1 (* k (fact (- k 1)))))])
    (fact 5)))
(define (compute) : Int fact-5)";

    [Fact]
    public void TopLevelLetrec_CSharp() =>
        Assert.Equal(120, CompileCSharpAndRunInt(TopLevelSource));

    [Fact]
    public void TopLevelLetrec_Il() => Assert.Equal(120, CompileIlAndRunInt(TopLevelSource));

    // --- Inside a class method ---

    // Lifted functions live at module level while the call site is a class method, which only
    // resolves because signatures are registered before any body is emitted. go(10, 5) = 60.
    private const string ClassMethodSource =
        @"(module test)
(define-class Counter
  [start : Int]
  (define (Run [n : Int]) : Int
    (letrec ([go (lambda ([k : Int] [acc : Int]) : Int
                   (if (= k 0) acc (go (- k 1) (+ acc k))))])
      (go n start))))
(define (compute) : Int
  (Counter/Run (new Counter 5) 10))";

    [Fact]
    public void LetrecInClassMethod_CSharp() =>
        Assert.Equal(60, CompileCSharpAndRunInt(ClassMethodSource));

    [Fact]
    public void LetrecInClassMethod_Il() => Assert.Equal(60, CompileIlAndRunInt(ClassMethodSource));

    // --- Inside an (object ...) constructor's super args ---

    // ObjectLifter decides which enclosing locals become fields of the synthesized class from the
    // object body's free variables, so a local that only a group reads has to be visible to that
    // scan — including in a super arg, which is emitted before `this` exists. When it was not, the
    // capture dangled and the IL backend failed with "Variable 'x' not found". Both shapes below
    // read `x`: the first group has no function bindings at all (it is only rewritten into a `let`
    // spine, nothing is lifted), the second has one.
    private const string LetrecInSuperArgsSource =
        @"(module test)
(define-class #:open Base
  [b : Int]
  (define (Get) : Int b))
(define (make [x : Int]) : Int
  (let ([o (object : Base
             (constructor (super (letrec ([v x]) v)))
             (define (M) : Int 1))])
    (Base/Get o)))
(define (compute) : Int (make 7))";

    [Fact]
    public void LetrecValueBindingInSuperArgs_CSharp() =>
        Assert.Equal(7, CompileCSharpAndRunInt(LetrecInSuperArgsSource));

    [Fact]
    public void LetrecValueBindingInSuperArgs_Il() =>
        Assert.Equal(7, CompileIlAndRunInt(LetrecInSuperArgsSource));

    // go(3) counts down to `x`, so 7 + 3 = 10.
    private const string LetrecFunctionInSuperArgsSource =
        @"(module test)
(define-class #:open Base
  [b : Int]
  (define (Get) : Int b))
(define (make [x : Int]) : Int
  (let ([o (object : Base
             (constructor (super (letrec ([go (lambda ([k : Int]) : Int
                                                (if (= k 0) x (+ 1 (go (- k 1)))))])
                                   (go 3))))
             (define (M) : Int 1))])
    (Base/Get o)))
(define (compute) : Int (make 7))";

    [Fact]
    public void LetrecFunctionBindingInSuperArgs_CSharp() =>
        Assert.Equal(10, CompileCSharpAndRunInt(LetrecFunctionInSuperArgsSource));

    [Fact]
    public void LetrecFunctionBindingInSuperArgs_Il() =>
        Assert.Equal(10, CompileIlAndRunInt(LetrecFunctionInSuperArgsSource));

    // --- Tail call optimization ---

    // A lifted self-call names the lifted function, so TailCallLowering turns it into a loop on
    // both backends. A million iterations would overflow the stack without that.
    private const string TcoSource =
        @"(module test)
(define (compute) : Int
  (letrec ([loop (lambda ([n : Int] [acc : Int]) : Int
                   (if (= n 0) acc (loop (- n 1) (+ acc 1))))])
    (loop 1000000 0)))";

    [Fact]
    public void TailRecursiveLetrec_RunsInConstantStack_CSharp() =>
        Assert.Equal(1000000, CompileCSharpAndRunInt(TcoSource));

    [Fact]
    public void TailRecursiveLetrec_RunsInConstantStack_Il() =>
        Assert.Equal(1000000, CompileIlAndRunInt(TcoSource));

    [Fact]
    public void TailRecursiveLetrec_EmitsALoopNotRecursion()
    {
        // Pins the mechanism rather than just the result: without the loop the million-iteration
        // test above would be the only thing standing between a regression and a stack overflow.
        var cs = CompileCSharp(TcoSource);
        Assert.Contains("while (true)", cs);
        Assert.Contains("continue;", cs);
    }

    // --- Mutual recursion at depth ---

    // Cross-calls between two lifted functions are not self-calls, so they do not become loops.
    // A moderate depth still confirms the two resolve each other correctly at runtime.
    private const string DeepMutualSource =
        @"(module test)
(define (compute) : Int
  (letrec ([ping (lambda ([n : Int]) : Int (if (= n 0) 0 (+ 1 (pong (- n 1)))))]
           [pong (lambda ([n : Int]) : Int (if (= n 0) 0 (ping (- n 1))))])
    (ping 1000)))";

    [Fact]
    public void DeepMutualRecursion_CSharp() =>
        Assert.Equal(500, CompileCSharpAndRunInt(DeepMutualSource));

    [Fact]
    public void DeepMutualRecursion_Il() => Assert.Equal(500, CompileIlAndRunInt(DeepMutualSource));

    // --- Inside a generic function ---

    // A group inside a generic function is lifted to a *generic* top-level static, which both
    // backends instantiate explicitly at the call site. `f` is a capture whose own type mentions
    // the type variable, which is the half of this that used to be rejected outright.
    // go(2, 1) applies f twice: 1 -> 6 -> 11.
    private const string GenericEnclosingSource =
        @"(module test)
(define (twice [x : ^a] [f : (^a -> ^a)]) : ^a
  (letrec ([go (lambda ([n : Int] [acc : ^a]) : ^a
                 (if (= n 0) acc (go (- n 1) (f acc))))])
    (go 2 x)))
(define (compute) : Int
  (twice 1 (lambda ([k : Int]) : Int (+ k 5))))";

    [Fact]
    public void GenericEnclosingFunction_CSharp() =>
        Assert.Equal(11, CompileCSharpAndRunInt(GenericEnclosingSource));

    [Fact]
    public void GenericEnclosingFunction_Il() =>
        Assert.Equal(11, CompileIlAndRunInt(GenericEnclosingSource));

    [Fact]
    public void GenericEnclosingFunction_LiftsToAGenericStatic()
    {
        // Pins the mechanism: the lifted function must declare the type parameter rather than
        // erase it to object, and the call site must instantiate it.
        var cs = CompileCSharp(GenericEnclosingSource);
        Assert.Contains("__letrec_test_0_go<", cs);
    }

    // Two type variables, one of which the group reaches only through a capture — so the lifted
    // signature mentions both, at indices that differ from the enclosing function's.
    private const string GenericTwoVarSource =
        @"(module test)
(define (conv [x : ^a] [f : (^a -> ^b)] [g : (^b -> ^a)]) : ^a
  (letrec ([step (lambda ([v : ^a]) : ^a (g (f v)))])
    (step x)))
(define (compute) : Int
  (conv 4 (lambda ([n : Int]) : Bool (> n 2)) (lambda ([b : Bool]) : Int (if b 7 0))))";

    [Fact]
    public void GenericTwoTypeVars_CSharp() =>
        Assert.Equal(7, CompileCSharpAndRunInt(GenericTwoVarSource));

    [Fact]
    public void GenericTwoTypeVars_Il() => Assert.Equal(7, CompileIlAndRunInt(GenericTwoVarSource));

    // A locally-polymorphic helper inside a NON-generic function, used at two instantiations.
    // InferLetrec generalizes the binding against the outer env, so its type has free variables
    // even though nothing generic is in sight — which the old blanket check also rejected.
    // (id 1) + (if (id #t) 2 3) = 1 + 2 = 3.
    private const string LocallyPolymorphicSource =
        @"(module test)
(define (compute) : Int
  (letrec ([id (lambda ([x : ^a]) : ^a x)])
    (+ (id 1) (if (id #t) 2 3))))";

    [Fact]
    public void LocallyPolymorphicHelper_CSharp() =>
        Assert.Equal(3, CompileCSharpAndRunInt(LocallyPolymorphicSource));

    [Fact]
    public void LocallyPolymorphicHelper_Il() =>
        Assert.Equal(3, CompileIlAndRunInt(LocallyPolymorphicSource));

    [Fact]
    public void GenericGroupMemberUsedAsAValue_ReportsError()
    {
        // The one shape that stays unrepresentable: IrNode.Closure has nowhere to carry the type
        // arguments a generic lifted function would need. The consumer has to be polymorphic too
        // — a `(Int -> Int)` parameter would just force `^a` to Int and leave nothing generic.
        var result = CompileWith(
            @"(module test)
(define (apply-to [g : (^b -> ^b)] [v : ^b]) : ^b (g v))
(define (h [x : ^a] [f : (^a -> ^a)]) : ^a
  (letrec ([use (lambda ([y : ^a]) : ^a (f y))])
    (apply-to use x)))
(define (compute) : Int (h 1 (lambda ([k : Int]) : Int (+ k 4))))",
            OutputMode.CSharp
        );

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("cannot be turned into a delegate")
        );
    }
}
