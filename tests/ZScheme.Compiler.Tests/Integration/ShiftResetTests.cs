using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

public class ShiftResetTests
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
        var dir = Path.GetDirectoryName(typeof(ShiftResetTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    [Fact]
    public void Reset_LowersToRuntimeReset()
    {
        var source =
            @"(module test)
(define (use-reset) : Int (reset 5))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.Reset", cs);
    }

    [Fact]
    public void Shift_LowersToRuntimeShiftTyped()
    {
        var source =
            @"(module test)
(define (use-shift) : Int (reset (shift k (k 10))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.ShiftTyped", cs);
    }

    [Fact]
    public void Shift_RequiresEnclosingReset()
    {
        var source =
            @"(module test)
(define (bad) : Int (shift k 1))";
        var (ok, _, diags) = Compile(source);
        Assert.False(ok);
        Assert.Contains(
            diags,
            d => d.Contains("(shift ...) used outside any enclosing (reset ...)")
        );
    }

    [Fact]
    public void ContinuationTransform_FiresOnShift()
    {
        // A non-tail shift inside a let triggers frame synthesis just like call/cc does.
        var source =
            @"(module test)
(define (use-shift) : Int
  (let ([t (reset (+ 1 (shift k (k 10))))])
    (+ t 1)))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));

        // Frame for the outer let around reset is synthesized. The CapturableCallHoister
        // also lifts the inner shift into a let-binding inside the reset thunk, so an
        // additional inner frame appears too — assert on the use-shift prefix rather than
        // a specific index, since both indices are present.
        Assert.Contains("__cont_use_shift_", cs);
        Assert.Contains("__Frame_use_shift_", cs);
        Assert.Contains(": ZScheme.Runtime.IFrame", cs);
        Assert.Contains("catch (ZScheme.Runtime.SaveContinuation", cs);
    }

    [Fact]
    public void ContinuationTransform_DoesNotFire_WhenResetUnused()
    {
        var source =
            @"(module test)
(define (plain-add [x : Int]) : Int (+ x 1))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.DoesNotContain("__cont_", cs);
        Assert.DoesNotContain("__Frame_", cs);
        Assert.DoesNotContain("SaveContinuation", cs);
    }

    [Fact]
    public void Reset_WithoutShift_StillCompiles()
    {
        // Reset with no shift in body: prompt is installed but never fires.
        var source =
            @"(module test)
(define (just-reset) : Int (reset 42))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.Reset", cs);
    }

    [Fact]
    public void Reset_FreeVariableCapture_Compiles()
    {
        var source =
            @"(module test)
(define (with-x [x : Int]) : Int
  (reset (+ x (shift k (k 1)))))";
        var (ok, _, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    [Fact]
    public void NestedResetAndShift_Compiles()
    {
        var source =
            @"(module test)
(define (nested) : Int
  (reset
    (+ (reset (shift k1 5))
       (shift k2 (k2 10)))))";
        var (ok, _, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
    }

    [Fact]
    public void CallCcAndShiftReset_CoexistInSameModule()
    {
        var source =
            @"(module test)
(define (with-callcc) : Int (call/cc (lambda (k) 42)))
(define (with-reset) : Int (reset (shift k (k 1))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.CallCcTyped", cs);
        Assert.Contains("Runtime.Reset", cs);
        Assert.Contains("Runtime.ShiftTyped", cs);
    }

    [Fact]
    public void Allows_ShiftInsideAsyncWithAwait()
    {
        var source =
            @"(module test)
(define-async (fetch) : (Task Int) 1)
(define-async (good) : (Task Int)
  (let ([v (await (fetch))])
    (reset (shift k (k v)))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.ShiftTyped", cs);
        Assert.Contains("Runtime.Reset", cs);
    }

    [Fact]
    public void Allows_ResetInsideAsyncWithAwait()
    {
        var source =
            @"(module test)
(define-async (fetch) : (Task Int) 1)
(define-async (good) : (Task Int)
  (let ([v (await (fetch))])
    (reset (+ v 1))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.Reset", cs);
    }
}
