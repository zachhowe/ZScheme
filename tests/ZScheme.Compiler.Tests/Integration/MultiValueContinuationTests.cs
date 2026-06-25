using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

/// <summary>
/// Integration coverage for MzScheme/Racket-style multi-value continuation invocation:
/// <c>(k v1 v2 …)</c> bundles its args into a tuple, and the same tuple becomes the
/// result of the enclosing capture form. Covers <c>call/cc</c>, <c>shift</c>, <c>reset</c>,
/// <c>control</c>, <c>prompt</c>, <c>call/comp</c>, plus the consumer forms
/// <c>let-values</c> and <c>call-with-values</c>.
/// </summary>
public class MultiValueContinuationTests
{
    private static (bool Success, string CsOutput, IReadOnlyList<string> Diagnostics) CompileCs(
        string source
    )
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        var diags = result.Diagnostics.Diagnostics.Select(d => d.ToString()).ToList();
        if (result is CompilationResult.CSharpOutputResult cs)
            return (result.Success, cs.CsOutput, diags);
        return (result.Success, "", diags);
    }

    private static (bool Success, byte[]? Bytes, IReadOnlyList<string> Diagnostics) CompileIl(
        string source
    )
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        var diags = result.Diagnostics.Diagnostics.Select(d => d.ToString()).ToList();
        if (result is CompilationResult.IlOutputResult il)
            return (result.Success, il.OutputBytes, diags);
        return (result.Success, null, diags);
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(MultiValueContinuationTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    /// <summary>
    /// Builds a delegate-typed invoker for a no-arg static method of return type T, bypassing
    /// the reflection layer that would otherwise wrap thrown exceptions in
    /// <see cref="TargetInvocationException"/>. Continuation programs throw
    /// <c>SaveContinuation</c> / <c>AbortAndResume</c> as their normal control-flow mechanism;
    /// <see cref="ZScheme.Runtime.Runtime.Run{T}"/> only catches the unwrapped exception types,
    /// so wrapped variants would leak.
    /// </summary>
    private static T InvokeNoArg<T>(byte[] bytes, string methodName)
    {
        var asm = Assembly.Load(bytes);
        var typeWithMethod = asm.GetExportedTypes()
            .First(t =>
                t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static) is not null
            );
        var method = typeWithMethod.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static
        )!;
        var del = (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), method);
        return del();
    }

    // ====== call/cc multi-value ======

    [Fact]
    public void CallCc_TwoArgs_BundlesIntoTuple()
    {
        var source =
            @"(module test)
(define (mv) : (Int * Int)
  (call/cc (lambda (k) (k 1 2))))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        // The continuation type is inferred as (int, int) → β; the call k(1, 2) becomes k((1, 2)).
        Assert.Contains("Func<(int, int)", cs);
        Assert.Contains("k((1, 2))", cs);
    }

    [Fact]
    public void CallCc_ThreeArgs_BundlesIntoTriple()
    {
        var source =
            @"(module test)
(define (mv) : (Int * Int * Int)
  (call/cc (lambda (k) (k 10 20 30))))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("k((10, 20, 30))", cs);
    }

    [Fact]
    public void CallCc_SevenArgs_MaxArity()
    {
        var source =
            @"(module test)
(define (mv) : (Int * Int * Int * Int * Int * Int * Int)
  (call/cc (lambda (k) (k 1 2 3 4 5 6 7))))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    [Fact]
    public void CallCc_EightArgs_RejectedWithValuesArityMessage()
    {
        var source =
            @"(module test)
(define (mv) : Int
  (call/cc (lambda (k) (k 1 2 3 4 5 6 7 8))))";
        var (ok, _, diags) = CompileCs(source);
        Assert.False(ok, "Expected arity-cap rejection");
        Assert.Contains(diags, d => d.Contains("at most 7"));
    }

    [Fact]
    public void CallCc_SingleValuePathPreserved()
    {
        // n=1 must not bundle — the existing single-value contract is intact.
        var source =
            @"(module test)
(define (sv) : Int (call/cc (lambda (k) (k 42))))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Func<int, int>", cs);
        Assert.Contains("k(42)", cs);
        Assert.DoesNotContain("k((42", cs);
    }

    [Fact]
    public void CallCc_NormalReturnViaValues_UnifiesWithMultiArgInvocation()
    {
        // f's normal return path produces a tuple; the multi-arg k call produces a tuple of
        // the same shape — both must unify against α.
        var source =
            @"(module test)
(define (mv [n : Int]) : (Int * Int)
  (call/cc (lambda (k) (if (= n 0) (k 1 2) (values 3 4)))))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    [Fact]
    public void CallCc_MixedArity_IsTypeError()
    {
        // (k 1) and (k 1 2) both invoke the same continuation — α can't be both T and Tuple[T,T].
        var source =
            @"(module test)
(define (bad [n : Int]) : Int
  (call/cc (lambda (k) (if (= n 0) (k 1) (k 1 2)))))";
        var (ok, _, _) = CompileCs(source);
        Assert.False(ok);
    }

    [Fact]
    public void CallCc_FreeVariableCapture_CombinesWithMultiValue()
    {
        var source =
            @"(module test)
(define (mv [seed : Int]) : (Int * Int)
  (call/cc (lambda (k) (k seed (+ seed 1)))))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    [Fact]
    public void CallCc_LetWrappedMultiValue_StillSynthesizesFrameAndContinuation()
    {
        // Multi-value invocation through a non-tail (let _ ...) context must still trigger
        // ContinuationTransform: the carried value is just a tuple object, but the frame
        // synthesis is identical to the single-value case.
        var source =
            @"(module test)
(define (mv) : (Int * Int)
  (let ([t (call/cc (lambda (k) (k 1 2)))])
    t))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("__cont_mv_0", cs);
        Assert.Contains("__Frame_mv_0", cs);
        Assert.Contains(": ZScheme.Runtime.IFrame", cs);
    }

    [Fact]
    public void CallCc_MultiValueLetRebindingPropagates()
    {
        // (let ([k2 k]) (k2 v1 v2)) — the marker must flow from k to k2.
        var source =
            @"(module test)
(define (mv) : (Int * Int)
  (call/cc (lambda (k)
    (let ([k2 k])
      (k2 7 8)))))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("k2((7, 8))", cs);
    }

    [Fact]
    public void CallCc_MarkerDoesNotPropagateThroughHelperFunction()
    {
        // Out-of-scope: passing k into a helper. The helper's parameter is a regular function
        // type, so calling it with multiple args fails to type-check (no auto-bundle).
        var source =
            @"(module test)
(define (apply-two [f : (Int Int -> Int)] [a : Int] [b : Int]) : Int (f a b))
(define (bad) : Int
  (call/cc (lambda (k) (apply-two k 1 2))))";
        var (ok, _, _) = CompileCs(source);
        // Either the inner type is wrong (k is unary) or call/cc unifies but apply-two won't accept k —
        // either way this is a type error.
        Assert.False(ok);
    }

    [Fact]
    public void CallCc_AllowsMultiValueInsideAsync()
    {
        // Multi-value capture inside an async body is supported: ContinuationTransform
        // splits at the let when call/cc isn't in tail position, and the synthesized
        // cont function takes the bundled tuple.
        var source =
            @"(module test)
(define-async (good) : (Task (Int * Int))
  (let ([t (call/cc (lambda (k) (k 1 2)))])
    (let-values ([(a b) t]) (values (+ a 1) (+ b 1)))))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.CallCcTyped", cs);
    }

    // ====== shift / reset multi-value ======

    [Fact]
    public void Shift_TwoArgs_BundlesIntoTuple()
    {
        var source =
            @"(module test)
(define (mv) : (Int * Int)
  (reset (shift k (k 10 20))))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Func<(int, int)", cs);
        Assert.Contains("k((10, 20))", cs);
    }

    [Fact]
    public void Shift_TaggedMultiValue()
    {
        var source =
            @"(module test)
(define (mv) : (Int * Int)
  (let ([tag (make-prompt-tag)])
    (reset tag (shift tag k (k 1 2)))))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    // ====== control / prompt multi-value ======

    [Fact]
    public void Control_TwoArgs_BundlesIntoTuple()
    {
        var source =
            @"(module test)
(define (mv) : (Int * Int)
  (prompt (control k (k 10 20))))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("k((10, 20))", cs);
    }

    [Fact]
    public void Control_TaggedMultiValue()
    {
        var source =
            @"(module test)
(define (mv) : (Int * Int)
  (let ([tag (make-prompt-tag)])
    (prompt tag (control tag k (k 4 5)))))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    // ====== call/comp multi-value ======

    [Fact]
    public void CallComp_TwoArgs_BundlesIntoTuple()
    {
        var source =
            @"(module test)
(define (mv) : (Int * Int)
  (prompt (call/comp (lambda (k) (k 10 20)))))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("k((10, 20))", cs);
    }

    [Fact]
    public void CallComp_TaggedMultiValue()
    {
        var source =
            @"(module test)
(define (mv) : (Int * Int)
  (let ([tag (make-prompt-tag)])
    (prompt tag (call/comp (lambda (k) (k 1 2)) tag))))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    // ====== let-values ======

    [Fact]
    public void LetValues_ArityTwo_DesugarsToMatch()
    {
        var source =
            @"(module test)
(define (use) : Int
  (let-values ([(a b) (values 1 2)]) (+ a b)))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        // Match-against-tuple is rendered as a switch expression in the C# emitter.
        Assert.Contains("(var a, var b)", cs);
    }

    [Fact]
    public void LetValues_ArityOne_DesugarsToPlainLet()
    {
        var source =
            @"(module test)
(define (use) : Int
  (let-values ([(x) 7]) (+ x 1)))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        // Single-name binding lowers to a plain let, NOT a switch.
        Assert.DoesNotContain("(var x)", cs);
        Assert.Contains("var x = 7", cs);
    }

    [Fact]
    public void LetValues_MultipleBindings_NestSequentially()
    {
        var source =
            @"(module test)
(define (use) : Int
  (let-values ([(a b) (values 1 2)]
               [(c) 3])
    (+ a (+ b c))))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    [Fact]
    public void LetValues_ArityMismatch_IsTypeError()
    {
        var source =
            @"(module test)
(define (bad) : Int
  (let-values ([(a b c) (values 1 2)]) a))";
        var (ok, _, _) = CompileCs(source);
        Assert.False(ok);
    }

    [Fact]
    public void LetValues_ComposesWithCallCcMultiValue()
    {
        var source =
            @"(module test)
(define (use) : Int
  (let-values ([(a b) (call/cc (lambda (k) (k 10 20)))])
    (+ a b)))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    // ====== call-with-values ======

    [Fact]
    public void CallWithValues_BasicArityTwo()
    {
        var source =
            @"(module test)
(define (use) : Int
  (call-with-values (lambda () (values 1 2)) (lambda (a b) (+ a b))))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("(var a, var b)", cs);
    }

    [Fact]
    public void CallWithValues_ConsumerArityOne()
    {
        var source =
            @"(module test)
(define (use) : Int
  (call-with-values (lambda () 42) (lambda (x) (+ x 1))))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    [Fact]
    public void CallWithValues_NonLiteralConsumer_Rejected()
    {
        var source =
            @"(module test)
(define (consumer [a : Int] [b : Int]) : Int (+ a b))
(define (bad) : Int
  (call-with-values (lambda () (values 1 2)) consumer))";
        var (ok, _, diags) = CompileCs(source);
        Assert.False(ok);
        Assert.Contains(diags, d => d.Contains("call-with-values") && d.Contains("literal"));
    }

    [Fact]
    public void CallWithValues_ProducerArityMismatch_IsTypeError()
    {
        var source =
            @"(module test)
(define (bad) : Int
  (call-with-values (lambda () (values 1 2 3)) (lambda (a b) (+ a b))))";
        var (ok, _, _) = CompileCs(source);
        Assert.False(ok);
    }

    [Fact]
    public void CallWithValues_ComposesWithCallCcMultiValue()
    {
        var source =
            @"(module test)
(define (use) : Int
  (call-with-values
    (lambda () (call/cc (lambda (k) (k 5 6 7))))
    (lambda (a b c) (+ a (+ b c)))))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    // ====== End-to-end execution (IL backend) ======

    [Fact]
    public void EndToEnd_CallCcMultiValue_CsBackend()
    {
        // The captured post-call context must live inside a (let _ <call> body) shape so
        // ContinuationTransform synthesizes a frame for it. Match scrutinees aren't wrapped
        // (tracked under v1 'Capturing context only fires around let' limitation), so we
        // bind the call result first and destructure inside.
        var source =
            @"(module test)
(define (mv) : Int
  (let ([t (call/cc (lambda (k) (k 10 20 30)))])
    (let-values ([(a b c) t]) (+ a (+ b c)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.NotNull(bytes);

        var result = ZScheme.Runtime.Runtime.Run(() => InvokeNoArg<int>(bytes!, "Mv"));
        Assert.Equal(60, result);
    }

    [Fact]
    public void EndToEnd_ShiftResetMultiValue()
    {
        var source =
            @"(module test)
(define (mv) : Int
  (reset
    (let ([t (shift k (k 7 8))])
      (let-values ([(a b) t]) (+ a b)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = ZScheme.Runtime.Runtime.Run(() => InvokeNoArg<int>(bytes!, "Mv"));
        Assert.Equal(15, result);
    }

    [Fact]
    public void EndToEnd_ControlPromptMultiValue()
    {
        var source =
            @"(module test)
(define (mv) : Int
  (prompt
    (let ([t (control k (k 100 200))])
      (let-values ([(a b) t]) (+ a b)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = ZScheme.Runtime.Runtime.Run(() => InvokeNoArg<int>(bytes!, "Mv"));
        Assert.Equal(300, result);
    }

    [Fact]
    public void EndToEnd_CallCompMultiValue()
    {
        var source =
            @"(module test)
(define (mv) : Int
  (prompt
    (let ([t (call/comp (lambda (k) (k 1 2 3)))])
      (let-values ([(a b c) t]) (+ a (+ b c))))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = ZScheme.Runtime.Runtime.Run(() => InvokeNoArg<int>(bytes!, "Mv"));
        Assert.Equal(6, result);
    }

    [Fact]
    public void EndToEnd_CallWithValuesMultiValueCallCc()
    {
        // call-with-values' producer thunk wraps the call/cc in a Lambda — the call/cc is in
        // tail position inside the thunk so its post-context is the thunk caller's let. Bind
        // the thunk-result first to give ContinuationTransform a let-call shape to wrap.
        var source =
            @"(module test)
(define (mv) : Int
  (let ([t (call/cc (lambda (k) (k 1 10 100)))])
    (call-with-values (lambda () t) (lambda (a b c) (+ a (+ b c))))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = ZScheme.Runtime.Runtime.Run(() => InvokeNoArg<int>(bytes!, "Mv"));
        Assert.Equal(111, result);
    }

    [Fact]
    public void EndToEnd_CallCcMultiValue_PostCallContextReplays()
    {
        // The frame-replay path threads the tuple through __Frame_*.Invoke and into the
        // post-call let context. Without auto-bundling at the rewrite layer, k(3, 4) would
        // be a 2-arg call and α would resolve to the wrong shape.
        var source =
            @"(module test)
(define (mv) : Int
  (let ([t (call/cc (lambda (k) (k 3 4)))])
    (let-values ([(a b) t]) (+ a (+ b 1)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = ZScheme.Runtime.Runtime.Run(() => InvokeNoArg<int>(bytes!, "Mv"));
        Assert.Equal(8, result);
    }
}
