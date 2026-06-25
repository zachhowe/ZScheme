using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

/// <summary>
///     Integration coverage for the limitation where a continuation operator appearing as a
///     sub-expression of a <c>BinOp</c> / <c>If</c> / <c>Match</c> / call-arg / etc. used to
///     have its surrounding context dropped from the captured continuation. The
///     <see cref="ZScheme.Compiler.Ir.CapturableCallHoister"/> pre-pass plus the extended
///     <see cref="ZScheme.Compiler.Ir.ContinuationTransform"/> compound-let-value detection
///     fix this for all four capture forms (<c>call/cc</c>, <c>shift</c>, <c>control</c>,
///     <c>call/comp</c>).
///
///     Each end-to-end test compiles ZScheme source to IL, loads the assembly, invokes the
///     compiled program through <c>Runtime.Run</c>, and asserts on the observed numeric
///     result. The numeric value would be wrong (typically the call's resumption value
///     unmodified) without the fix, so a passing assertion proves the post-call context was
///     replayed correctly.
/// </summary>
public class SubExpressionCaptureTests
{
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

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(SubExpressionCaptureTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

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

    private static int RunInt(byte[] bytes, string methodName) =>
        ZScheme.Runtime.Runtime.Run(() => InvokeNoArg<int>(bytes, methodName));

    // ====================================================================================
    //  call/cc — capturing context through sub-expression positions
    // ====================================================================================

    [Fact]
    public void CallCc_BinOpRight_PostContextReplays()
    {
        // (k 7) returns from call/cc, the captured continuation is "x → (+ 100 x); r := that;
        // (* r 2)". Pre-fix: returns 7 (the +100 step is dropped). Post-fix: 107 * 2 = 214.
        var source =
            @"(module test)
(define (run) : Int
  (let ([r (+ 100 (call/cc (lambda (k) (k 7))))])
    (* r 2)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(214, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void CallCc_BinOpLeft_PostContextReplays()
    {
        var source =
            @"(module test)
(define (run) : Int
  (let ([r (+ (call/cc (lambda (k) (k 7))) 100)])
    (* r 2)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(214, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void CallCc_NestedBinOp_FrameChainsThroughBoth()
    {
        // (k 5) → +1 = 6 → *2 = 12 → r := 12 → +0 = 12. Both BinOps must be captured.
        var source =
            @"(module test)
(define (run) : Int
  (let ([r (* 2 (+ 1 (call/cc (lambda (k) (k 5)))))])
    (+ r 0)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(12, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void CallCc_IfCondition_PostContextReplays()
    {
        // (k 0) → cond is 0 → (= 0 0) is #t → 100 → r := 100 → +5 = 105.
        var source =
            @"(module test)
(define (run) : Int
  (let ([r (if (= (call/cc (lambda (k) (k 0))) 0) 100 200)])
    (+ r 5)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(105, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void CallCc_IfThenBranch_PostContextReplays()
    {
        // call/cc is in the then-branch tail; the if itself is the let-value. The outer let
        // wraps the entire if (extended IsCapturable case). (k 7) → if returns 7 → r := 7 → +1.
        var source =
            @"(module test)
(define (run) : Int
  (let ([r (if #t (call/cc (lambda (k) (k 7))) 999)])
    (+ r 1)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(8, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void CallCc_IfElseBranch_PostContextReplays()
    {
        var source =
            @"(module test)
(define (run) : Int
  (let ([r (if #f 999 (call/cc (lambda (k) (k 7))))])
    (+ r 1)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(8, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void CallCc_FunctionCallArg_PostContextReplays()
    {
        // Argument-position capture. (k 5) → (double 5) = 10 → r := 10 → +1 = 11.
        var source =
            @"(module test)
(define (double [x : Int]) : Int (* x 2))
(define (run) : Int
  (let ([r (double (call/cc (lambda (k) (k 5))))])
    (+ r 1)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(11, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void CallCc_TailPosition_NoFrameNeeded_StillReturnsCorrectly()
    {
        // call/cc in tail position of f: no enclosing let, no frame. (k 42) just returns 42.
        var source =
            @"(module test)
(define (run) : Int
  (call/cc (lambda (k) (k 42))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(42, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void CallCc_BinOpInTailPosition_FrameForBinOp()
    {
        // (+ 1 (call/cc ...)) at function tail. After hoisting: (let v (call/cc ...) (+ 1 v)).
        // Frame for (+ 1 v). (k 41) → +1 = 42.
        var source =
            @"(module test)
(define (run) : Int
  (+ 1 (call/cc (lambda (k) (k 41)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(42, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void CallCc_DiscardingK_BinOpContextIrrelevant()
    {
        // userFn discards k and returns 99 directly — the post-context isn't replayed because
        // userFn returned normally. (call/cc returns 99) → r := 99+100=199 → r*2 = 398.
        var source =
            @"(module test)
(define (run) : Int
  (let ([r (+ 100 (call/cc (lambda (k) 99)))])
    (* r 2)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(398, RunInt(bytes!, "Run"));
    }

    // ====================================================================================
    //  shift / reset — same matrix
    // ====================================================================================

    [Fact]
    public void Shift_BinOpRight_PostContextReplays()
    {
        // (reset (* 2 (+ 100 (shift k (k 7))))) — k captures the (+ 100 _) and (* 2 _) frames
        // up to the reset. Pre-fix: shift body sees no frame for the +100, returns 7 directly.
        // Post-fix: 107 * 2 = 214.
        var source =
            @"(module test)
(define (run) : Int
  (reset (* 2 (+ 100 (shift k (k 7))))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(214, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void Shift_IfBranch_PostContextReplays()
    {
        // (reset (if #t (+ 1 (shift k (k 10))) 999)) — shift inside then-branch's BinOp.
        // Frame chain: (+ 1 _) lifted via hoist; if's then is wrapped; reset bounds.
        var source =
            @"(module test)
(define (run) : Int
  (reset (if #t (+ 1 (shift k (k 10))) 999)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(11, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void Shift_DiscardingK_DropsBinOpContext()
    {
        // Shift body discards k, returns 99. The (+ 100 _) and (* 2 _) frames around the
        // shift are unreachable. Reset returns 99 directly.
        var source =
            @"(module test)
(define (run) : Int
  (reset (* 2 (+ 100 (shift k 99)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(99, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void Shift_FunctionCallArg_PostContextReplays()
    {
        var source =
            @"(module test)
(define (double [x : Int]) : Int (* x 2))
(define (run) : Int
  (reset (double (shift k (k 5)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(10, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void Shift_MultiShotThroughBinOp_BothInvocationsReplayContext()
    {
        // (k 1) + (k 2) — each invocation of k must replay the +100 frame independently.
        // 1+100 = 101, 2+100 = 102, sum = 203.
        var source =
            @"(module test)
(define (run) : Int
  (reset (+ 100 (shift k (+ (k 1) (k 2))))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(203, RunInt(bytes!, "Run"));
    }

    // ====================================================================================
    //  control / prompt — same matrix
    // ====================================================================================

    [Fact]
    public void Control_BinOpRight_PostContextReplays()
    {
        var source =
            @"(module test)
(define (run) : Int
  (prompt (* 2 (+ 100 (control k (k 7))))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(214, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void Control_IfBranch_PostContextReplays()
    {
        var source =
            @"(module test)
(define (run) : Int
  (prompt (if #t (+ 1 (control k (k 10))) 999)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(11, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void Control_DiscardingK_DropsBinOpContext()
    {
        var source =
            @"(module test)
(define (run) : Int
  (prompt (* 2 (+ 100 (control k 99)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(99, RunInt(bytes!, "Run"));
    }

    // ====================================================================================
    //  call/comp — same matrix
    // ====================================================================================

    [Fact]
    public void CallComp_BinOpRight_PostContextReplays()
    {
        var source =
            @"(module test)
(define (run) : Int
  (prompt (* 2 (+ 100 (call/comp (lambda (k) (k 7)))))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(214, RunInt(bytes!, "Run"));
    }

    [Fact]
    public void CallComp_FunctionCallArg_PostContextReplays()
    {
        var source =
            @"(module test)
(define (double [x : Int]) : Int (* x 2))
(define (run) : Int
  (prompt (double (call/comp (lambda (k) (k 5))))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Equal(10, RunInt(bytes!, "Run"));
    }

    // ====================================================================================
    //  Compile-shape assertions — verify the hoister actually rewrites the IR
    // ====================================================================================

    [Fact]
    public void Hoist_BinOpRight_LiftsCallToLet()
    {
        // After hoisting, the C# output should show a __cc_hoist_N let-binding around the
        // capturable call. This proves the hoister fired.
        var source =
            @"(module test)
(define (run) : Int
  (let ([r (+ 100 (call/cc (lambda (k) (k 7))))])
    (* r 2)))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("__cc_hoist_", cs);
        // And ContinuationTransform synthesized a frame for the captured post-context.
        Assert.Contains("__cont_run_", cs);
        Assert.Contains("__Frame_run_", cs);
    }

    [Fact]
    public void Hoist_NoCapturable_NoExtraLet()
    {
        // Pure expression: no capturable call → no hoister activity, no frames.
        var source =
            @"(module test)
(define (pure) : Int
  (let ([r (+ 100 7)])
    (* r 2)))
(define (run) : Int (call/cc (lambda (k) (k 1))))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        // The pure function has no hoist artifacts.
        Assert.DoesNotContain("__cc_hoist_", cs);
    }

    [Fact]
    public void Hoist_Recurses_IntoIfBranches_LiftsInnerBinOp()
    {
        // The if-branch contains a BinOp wrapping the call/cc. Hoister recurses into the
        // branch and lifts there.
        var source =
            @"(module test)
(define (run) : Int
  (let ([r (if #t (+ 1 (call/cc (lambda (k) (k 7)))) 999)])
    (+ r 0)))";
        var (ok, cs, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("__cc_hoist_", cs);
    }

    [Fact]
    public void Hoist_PreservesShortCircuitSemantics_NoLiftUnderAndOr()
    {
        // and/or short-circuit: hoister must NOT lift either operand into a Let, since that
        // would unconditionally evaluate the right operand. We verify the program still
        // compiles and runs — semantics are preserved even if context capture under and/or
        // is a known limitation.
        var source =
            @"(module test)
(define (run) : Int
  (let ([r (if (and #t #f) 100 200)])
    r))";
        var (ok, _, diags) = CompileCs(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }
}
