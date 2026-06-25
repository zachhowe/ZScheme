using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

/// <summary>
/// End-to-end coverage for continuation operators inside <c>[async]</c> functions. Each test
/// compiles a ZScheme program that mixes <c>call/cc</c> / <c>shift</c> / <c>reset</c> /
/// <c>control</c> / <c>call/comp</c> with <c>await</c>, then drives it through
/// <see cref="ZScheme.Runtime.Runtime.RunAsync{T}"/> so async-tail frames replay via the
/// non-blocking <see cref="ZScheme.Runtime.Runtime.ResumeAsync"/> path.
/// </summary>
public class AsyncContinuationEndToEndTests
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

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(AsyncContinuationEndToEndTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    /// <summary>
    /// Builds a delegate-typed invoker for a no-arg static method, bypassing the reflection
    /// layer that wraps thrown exceptions in <see cref="TargetInvocationException"/>. Same
    /// rationale as <c>MultiValueContinuationTests.InvokeNoArg&lt;T&gt;</c>.
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

    [Fact]
    public async Task CallCc_NormalReturn_AwaitAfter_ReplaysAsyncFrame()
    {
        // Non-tail call/cc with an await downstream — the synthesized __cont function is
        // marked async, the parent body awaits it, and the frame class implements
        // InvokeAsync. RunAsync drives ResumeAsync without blocking.
        var source =
            @"(module test)
(define-async (fetch [x : Int]) : (Task Int) (+ x 1))
(define-async (good) : (Task Int)
  (let ([v (call/cc (lambda (k) (k 41)))])
    (await (fetch v))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.NotNull(bytes);

        // call/cc returns 41 (the user fn calls the continuation immediately with 41), the
        // captured continuation runs the rest: await fetch(41) → 42.
        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task CallCc_NoAwaitAfter_StaysSync_StillWorks()
    {
        // Non-tail call/cc with sync rest — the synthesized cont stays sync. Still drive
        // through RunAsync to verify async-aware path works alongside sync frames.
        var source =
            @"(module test)
(define-async (good) : (Task Int)
  (let ([v (call/cc (lambda (k) (k 41)))])
    (+ v 1)))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task CallCc_AwaitBefore_TailPosition_NoFrameNeeded()
    {
        // call/cc in tail position — no frame to synthesize; the SaveContinuation throw
        // propagates out and RunAsync catches it.
        var source =
            @"(module test)
(define-async (fetch) : (Task Int) 1)
(define-async (good) : (Task Int)
  (let ([v (await (fetch))])
    (call/cc (lambda (k) (k v)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task ShiftReset_InsideAsync_ResumesUnderFreshPrompt()
    {
        // shift inside reset, with the reset entirely inside the async body. The captured
        // continuation is delimited by reset; the user fn invokes k once.
        var source =
            @"(module test)
(define-async (fetch) : (Task Int) 5)
(define-async (good) : (Task Int)
  (let ([v (await (fetch))])
    (reset (let ([r (shift k (k v))]) (+ r 10)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        // shift captures `(+ r 10)` as the delimited continuation; (k v) replays it with 5 → 15.
        Assert.Equal(15, result);
    }

    [Fact]
    public async Task ControlPrompt_InsideAsync_ResumesInCallerContext()
    {
        var source =
            @"(module test)
(define-async (fetch) : (Task Int) 7)
(define-async (good) : (Task Int)
  (let ([v (await (fetch))])
    (prompt (let ([r (control k (k v))]) (* r 2)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        // control captures `(* r 2)` and resumes immediately with 7 → 14.
        Assert.Equal(14, result);
    }

    [Fact]
    public async Task CallComp_InsideAsync_RacketStyle()
    {
        var source =
            @"(module test)
(define-async (good) : (Task Int)
  (prompt (let ([t (call/comp (lambda (k) (k 100)))]) (+ t 1))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        Assert.Equal(101, result);
    }

    [Fact]
    public async Task CallCc_AwaitBeforeAndAfter_BothFramesParticipate()
    {
        // await prefix → call/cc → await suffix. Both lets are non-tail; the second cont
        // is async because it awaits, and the first cont is also async because its body
        // contains the second await further down the chain.
        var source =
            @"(module test)
(define-async (fetch [n : Int]) : (Task Int) (+ n 1))
(define-async (good) : (Task Int)
  (let ([a (await (fetch 1))])
    (let ([v (call/cc (lambda (k) (k a)))])
      (await (fetch v)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        // (fetch 1) → 2; call/cc returns 2; (fetch 2) → 3.
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task CallCc_TwoCaptures_Chained()
    {
        var source =
            @"(module test)
(define-async (fetch [n : Int]) : (Task Int) (+ n 1))
(define-async (good) : (Task Int)
  (let ([a (call/cc (lambda (k) (k 10)))])
    (let ([b (call/cc (lambda (k) (k 20)))])
      (await (fetch (+ a b))))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        // a=10, b=20, (fetch 30) → 31.
        Assert.Equal(31, result);
    }

    [Fact]
    public async Task TransitiveCallCc_ThroughSyncHelper_FromAsyncCaller()
    {
        // Async function calls a sync helper that itself uses call/cc. Frames stack:
        // helper's frame (post-call/cc inside helper) + async caller's frame (post-helper).
        var source =
            @"(module test)
(define (helper [x : Int]) : Int
  (let ([v (call/cc (lambda (k) (k x)))])
    (+ v 1)))
(define-async (fetch) : (Task Int) 100)
(define-async (good) : (Task Int)
  (let ([a (await (fetch))])
    (let ([b (helper a)])
      (+ b 5))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        // a=100; helper(100): v=100, then +1 = 101. Then 101 + 5 = 106.
        Assert.Equal(106, result);
    }

    [Fact]
    public async Task ShiftReset_InsideAsync_DoubleK_AccumulatesAcrossCaptures()
    {
        // The captured delimited continuation is invoked twice — classic shift/reset
        // multiplication pattern. Each invocation re-runs the delimited body.
        var source =
            @"(module test)
(define-async (fetch) : (Task Int) 1)
(define-async (good) : (Task Int)
  (let ([v (await (fetch))])
    (reset (let ([r (shift k (+ (k v) (k v)))]) (* r 2)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        // (* r 2) is captured as k. v=1. (k 1) = 2 each. (+ (k v) (k v)) = 4.
        Assert.Equal(4, result);
    }

    [Fact]
    public async Task TaggedShiftReset_InsideAsync()
    {
        var source =
            @"(module test)
(define-async (good) : (Task Int)
  (let ([tag (make-prompt-tag)])
    (reset tag (let ([r (shift tag k (k 7))]) (+ r 3)))))";
        var (ok, bytes, diags) = CompileIl(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        var result = await ZScheme.Runtime.Runtime.RunAsync(() =>
            InvokeNoArg<Task<int>>(bytes!, "Good")
        );
        Assert.Equal(10, result);
    }
}
