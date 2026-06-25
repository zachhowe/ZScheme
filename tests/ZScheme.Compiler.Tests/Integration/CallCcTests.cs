using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

public class CallCcTests
{
    private static (bool Success, string CsOutput, IReadOnlyList<string> Diagnostics) Compile(
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
        var dir = Path.GetDirectoryName(typeof(CallCcTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    [Fact]
    public void CallCc_ParsesAndCompiles()
    {
        var source =
            @"(module test)
(define (early-exit [n : Int]) : Int
  (call/cc (lambda (k) (if (= n 0) (k 99) n))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        // Lowered to a CallCcTyped invocation against the runtime.
        Assert.Contains("Runtime.CallCcTyped", cs);
    }

    [Fact]
    public void CallCc_TypeChecks_ResultMatchesUserFnReturnType()
    {
        var source =
            @"(module test)
(define (use-callcc) : Int
  (call/cc (lambda (k) 42)))";
        var (ok, _, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    [Fact]
    public void CallCc_ContinuationCallReturnTypeCanBeAnything()
    {
        // The continuation k returns β, a universally polymorphic type — calling (k v)
        // can be unified with any expected type at the call site.
        var source =
            @"(module test)
(define (mixed-ret [n : Int]) : Int
  (call/cc (lambda (k) (if (= n 0) (k 1) (+ 2 3)))))";
        var (ok, _, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    [Fact]
    public void CallCc_RequiresExactlyOneArgument()
    {
        var source =
            @"(module test)
(define (bad-arity) : Int
  (call/cc))";
        var (ok, _, _) = Compile(source);
        Assert.False(ok);
    }

    [Fact]
    public void CallCc_RejectsNonFunctionArgument()
    {
        var source =
            @"(module test)
(define (bad-arg) : Int
  (call/cc 42))";
        var (ok, _, _) = Compile(source);
        Assert.False(ok);
    }

    [Fact]
    public void ContinuationTransform_GeneratesContFnAndFrameClass_ForLetWrappedCall()
    {
        // ContinuationTransform fires on (let ([t (call/cc ...)]) body) — the call is non-tail.
        // It should emit a sibling continuation function and a frame class, and wrap the
        // original Let in a try/catch.
        var source =
            @"(module test)
(define (use-callcc) : Int
  (let ([t (call/cc (lambda (k) (k 41)))])
    (+ t 1)))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        Assert.Contains("__cont_use_callcc_0", cs);
        Assert.Contains("__Frame_use_callcc_0", cs);
        Assert.Contains(": ZScheme.Runtime.IFrame", cs);
        Assert.Contains("catch (ZScheme.Runtime.SaveContinuation", cs);
        Assert.Contains(".Extend(new __Frame_use_callcc_0", cs);
        Assert.Contains("throw __sce", cs);
    }

    [Fact]
    public void ContinuationTransform_DoesNotFire_WhenCallCcUnused()
    {
        var source =
            @"(module test)
(define (plain-add [x : Int]) : Int (+ x 1))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        // No ContinuationTransform artifacts should appear.
        Assert.DoesNotContain("__cont_", cs);
        Assert.DoesNotContain("__Frame_", cs);
        Assert.DoesNotContain("SaveContinuation", cs);
    }

    [Fact]
    public void Allows_CallCcInsideAsyncWithAwait_TailPosition()
    {
        // call/cc in tail position of an async body — no synthesized cont needed
        // (nothing after the call to capture), but compilation must succeed.
        var source =
            @"(module test)
(define-async (fetch) : (Task Int) 1)
(define-async (good) : (Task Int)
  (let ([v (await (fetch))])
    (call/cc (lambda (k) (k v)))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.CallCcTyped", cs);
    }

    [Fact]
    public void Allows_CallCcInsideAsyncWithAwait_NonTailPosition_GeneratesAsyncCont()
    {
        // call/cc in a non-tail let.Value, with post-call code — the post-call code is
        // pure-sync here, so the synthesized cont function stays sync (no IsAsync).
        var source =
            @"(module test)
(define-async (fetch) : (Task Int) 1)
(define-async (good) : (Task Int)
  (let ([v (call/cc (lambda (k) (k 41)))])
    (+ v 1)))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.CallCcTyped", cs);
        Assert.Contains("__cont_good_", cs);
        Assert.Contains("__Frame_good_", cs);
    }

    [Fact]
    public void Allows_CallCcWithAwaitAfter_GeneratesAsyncContAndInvokeAsync()
    {
        // call/cc in non-tail let.Value, with an await downstream — the synthesized cont
        // function must be async (IsAsync=true), the parent body awaits the cont call,
        // and the frame class emits InvokeAsync for ResumeAsync.
        var source =
            @"(module test)
(define-async (fetch [x : Int]) : (Task Int) (+ x 1))
(define-async (good) : (Task Int)
  (let ([v (call/cc (lambda (k) (k 41)))])
    (await (fetch v))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.CallCcTyped", cs);
        Assert.Contains("__cont_good_", cs);
        Assert.Contains("__Frame_good_", cs);
        // The synthesized cont function is async — emitted with `async Task<int>`.
        Assert.Contains("async System.Threading.Tasks.Task<int> __cont_good_", cs);
        // The frame class exposes InvokeAsync (driven by Runtime.ResumeAsync).
        Assert.Contains("InvokeAsync", cs);
    }

    [Fact]
    public void Allows_CallCcTransitivelyReachedFromAsync()
    {
        // Async function calls a sync helper that itself uses call/cc. The non-tail call
        // to the helper inside the async body is wrapped with a SaveContinuation handler
        // that extends the in-flight exception with this call site's frame; the helper's
        // continuation extends frames the same way. Both compile.
        var source =
            @"(module test)
(define (helper [x : Int]) : Int
  (call/cc (lambda (k) (k x))))
(define-async (fetch) : (Task Int) 1)
(define-async (driver) : (Task Int)
  (let ([a (await (fetch))])
    (let ([v (helper a)])
      (+ v 1))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        // The non-tail helper call gets wrapped with a SaveContinuation handler.
        Assert.Contains("SaveContinuation", cs);
        Assert.Contains("__Frame_driver_", cs);
    }

    [Fact]
    public void Allows_CallCcInNonAsyncFunctionAlongsideAsyncFunctions()
    {
        // call/cc in a regular function and an async function with await must coexist
        // when the async function does NOT reach call/cc.
        var source =
            @"(module test)
(define (use-cc [x : Int]) : Int
  (call/cc (lambda (k) (k x))))
(define-async (fetch) : (Task Int) 1)
(define-async (good) : (Task Int)
  (let ([v (await (fetch))])
    (+ v 1)))";
        var (ok, _, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }
}
