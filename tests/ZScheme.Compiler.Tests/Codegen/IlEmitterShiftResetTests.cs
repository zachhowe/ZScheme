using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Codegen;

public class IlEmitterShiftResetTests
{
    private static (bool Success, byte[] Bytes, IReadOnlyList<string> Diagnostics) Compile(
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
        return (result.Success, [], diags);
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(IlEmitterShiftResetTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static MethodInfo FindMethod(Assembly asm, string name)
    {
        return asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length is 0 or 1
            );
    }

    private static T RunInIl<T>(Assembly asm, string methodName, params object?[] args)
    {
        var method = FindMethod(asm, methodName);
        return ZScheme.Runtime.Runtime.Run<T>(() =>
            (T)
                method.Invoke(
                    null,
                    BindingFlags.DoNotWrapExceptions,
                    binder: null,
                    parameters: args,
                    culture: null
                )!
        );
    }

    [Fact]
    public void Reset_WithoutShift_ReturnsBody()
    {
        var source =
            @"(module test)
(define (just-reset) : Int (reset 42))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));

        var asm = Assembly.Load(bytes);
        Assert.Equal(42, RunInIl<int>(asm, "JustReset"));
    }

    [Fact]
    public void Shift_DiscardingK_YieldsShiftBody()
    {
        // ContinuationTransform's v1 only wraps Let(t, NonTailCall, body) shapes — same
        // restriction as call/cc. Express composability using explicit let-bindings.
        // Equivalent to: (* 2 (reset (let ([v (shift k 10)]) (+ 1 v)))) — k discarded, "+ 1 _"
        // frame thrown away, reset returns 10, * 2 = 20.
        var source =
            @"(module test)
(define (use-shift) : Int
  (let ([r (reset
            (let ([v (shift k 10)])
              (+ 1 v)))])
    (* 2 r)))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));

        var asm = Assembly.Load(bytes);
        Assert.Equal(20, RunInIl<int>(asm, "UseShift"));
    }

    [Fact]
    public void Shift_ComposedK_ReplaysCapturedFrame()
    {
        // (* 2 (reset (let ([v (shift k (k 10))]) (+ 1 v)))) — k captures "+ 1 _", k(10) = 11,
        // * 2 = 22.
        var source =
            @"(module test)
(define (use-shift) : Int
  (let ([r (reset
            (let ([v (shift k (k 10))])
              (+ 1 v)))])
    (* 2 r)))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));

        var asm = Assembly.Load(bytes);
        Assert.Equal(22, RunInIl<int>(asm, "UseShift"));
    }

    [Fact]
    public void Shift_MultiShot_InvokesKMultipleTimes()
    {
        // No surrounding non-tail call inside the reset, so k captures no frames; k(v) = v
        // and the body evaluates (+ (k 1) (k 2)) = 3.
        var source =
            @"(module test)
(define (use-shift) : Int
  (reset (shift k (+ (k 1) (k 2)))))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));

        var asm = Assembly.Load(bytes);
        Assert.Equal(3, RunInIl<int>(asm, "UseShift"));
    }

    [Fact]
    public void Shift_FreeVariableCapture_ResumesWithCapturedValue()
    {
        // (reset (let ([v (shift k (k 1))]) (+ x v))) — captured x rides along in the frame.
        var source =
            @"(module test)
(define (with-x [x : Int]) : Int
  (reset
    (let ([v (shift k (k 1))])
      (+ x v))))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));

        var asm = Assembly.Load(bytes);
        Assert.Equal(101, RunInIl<int>(asm, "WithX", 100));
    }

    [Fact]
    public void NestedReset_InnerShiftTargetsInnermost()
    {
        // Inner reset evaluates to 99 (k1 discarded). Outer reset's body lets that bind to a,
        // then captures via shift k2 (k2 10): the k2 captures "+ a _" frame; k2(10) = a+10 = 109.
        var source =
            @"(module test)
(define (nested) : Int
  (reset
    (let ([a (reset (shift k1 99))])
      (let ([v (shift k2 (k2 10))])
        (+ a v)))))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));

        var asm = Assembly.Load(bytes);
        Assert.Equal(109, RunInIl<int>(asm, "Nested"));
    }

    [Fact]
    public void CallCcAndShift_CoexistInSameProgram()
    {
        // Ensures the two operators don't interfere when both used in the same module.
        var source =
            @"(module test)
(define (cc-only) : Int (call/cc (lambda (k) 41)))
(define (shift-only) : Int (reset (shift k (k 1))))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));

        var asm = Assembly.Load(bytes);
        Assert.Equal(41, RunInIl<int>(asm, "CcOnly"));
        Assert.Equal(1, RunInIl<int>(asm, "ShiftOnly"));
    }
}
