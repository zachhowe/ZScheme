using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

public class ControlPromptTests
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
        var dir = Path.GetDirectoryName(typeof(ControlPromptTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    [Fact]
    public void Prompt_LowersToRuntimeReset()
    {
        // (prompt e) is a surface alias for (reset e); both lower to Runtime.Reset.
        var source =
            @"(module test)
(define (use-prompt) : Int (prompt 5))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.Reset", cs);
    }

    [Fact]
    public void Control_LowersToRuntimeControlTyped()
    {
        var source =
            @"(module test)
(define (use-control) : Int (prompt (control k (k 10))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.ControlTyped", cs);
    }

    [Fact]
    public void CallComp_LowersToRuntimeCallCompTyped()
    {
        var source =
            @"(module test)
(define (use-callcomp) : Int (prompt (call/comp (lambda (k) (k 10)))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.CallCompTyped", cs);
    }

    [Fact]
    public void Control_RequiresEnclosingPrompt()
    {
        var source =
            @"(module test)
(define (bad) : Int (control k 1))";
        var (ok, _, diags) = Compile(source);
        Assert.False(ok);
        Assert.Contains(diags, d => d.Contains("(control"));
    }

    [Fact]
    public void CallComp_RequiresEnclosingPrompt()
    {
        var source =
            @"(module test)
(define (bad) : Int (call/comp (lambda (k) (k 1))))";
        var (ok, _, diags) = Compile(source);
        Assert.False(ok);
        Assert.Contains(diags, d => d.Contains("(call/comp"));
    }

    [Fact]
    public void MakePromptTag_TypesAsPromptTag()
    {
        var source =
            @"(module test)
(define (mk) : Int
  (let ([t (make-prompt-tag)])
    (prompt t 1)))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.MakePromptTag", cs);
        Assert.Contains("Runtime.ResetAt", cs);
    }

    [Fact]
    public void TaggedShift_LowersToShiftTypedAt()
    {
        var source =
            @"(module test)
(define (use-tagged) : Int
  (let ([t (make-prompt-tag)])
    (reset t (shift t k (k 10)))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.ShiftTypedAt", cs);
    }

    [Fact]
    public void TaggedControl_LowersToControlTypedAt()
    {
        var source =
            @"(module test)
(define (use-tagged) : Int
  (let ([t (make-prompt-tag)])
    (prompt t (control t k (k 10)))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.ControlTypedAt", cs);
    }

    [Fact]
    public void TaggedCallComp_LowersToCallCompTypedAt()
    {
        var source =
            @"(module test)
(define (use-tagged) : Int
  (let ([t (make-prompt-tag)])
    (prompt t (call/comp (lambda (k) (k 10)) t))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.CallCompTypedAt", cs);
    }

    [Fact]
    public void TagArgumentMustBePromptTag()
    {
        // Passing an Int where PromptTag is expected — type unification error.
        var source =
            @"(module test)
(define (bad) : Int (prompt 5 1))";
        var (ok, _, diags) = Compile(source);
        Assert.False(ok);
    }

    [Fact]
    public void ContinuationTransform_FiresOnControl()
    {
        // Like shift, control inside a non-tail let triggers frame synthesis.
        var source =
            @"(module test)
(define (use-control) : Int
  (let ([t (prompt (+ 1 (control k (k 10))))])
    (+ t 1)))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        // CapturableCallHoister also lifts the inner control into a let-binding inside the
        // prompt thunk, producing an additional frame; match on the use-control prefix.
        Assert.Contains("__cont_use_control_", cs);
        Assert.Contains(": ZScheme.Runtime.IFrame", cs);
        Assert.Contains("catch (ZScheme.Runtime.SaveContinuation", cs);
    }

    [Fact]
    public void AllOperators_CoexistInSameModule()
    {
        var source =
            @"(module test)
(define (a) : Int (call/cc (lambda (k) 1)))
(define (b) : Int (reset (shift k 2)))
(define (c) : Int (prompt (control k 3)))
(define (d) : Int (prompt (call/comp (lambda (k) 4))))
(define (e) : Int
  (let ([t (make-prompt-tag)])
    (prompt t (control t k 5))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.CallCcTyped", cs);
        Assert.Contains("Runtime.ShiftTyped", cs);
        Assert.Contains("Runtime.ControlTyped", cs);
        Assert.Contains("Runtime.CallCompTyped", cs);
        Assert.Contains("Runtime.ControlTypedAt", cs);
        Assert.Contains("Runtime.MakePromptTag", cs);
    }

    [Fact]
    public void Allows_ControlInsideAsyncWithAwait()
    {
        var source =
            @"(module test)
(define-async (fetch) : (Task Int) 1)
(define-async (good) : (Task Int)
  (let ([v (await (fetch))])
    (prompt (control k (k v)))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.ControlTyped", cs);
    }

    [Fact]
    public void Allows_CallCompInsideAsyncWithAwait()
    {
        var source =
            @"(module test)
(define-async (fetch) : (Task Int) 1)
(define-async (good) : (Task Int)
  (let ([v (await (fetch))])
    (prompt (call/comp (lambda (k) (k v))))))";
        var (ok, cs, diags) = Compile(source);
        Assert.True(ok, "Compilation failed: " + string.Join("\n", diags));
        Assert.Contains("Runtime.CallCompTyped", cs);
    }
}
