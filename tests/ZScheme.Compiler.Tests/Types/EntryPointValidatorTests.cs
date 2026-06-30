using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Types;

/// <summary>
///     Exercises <see cref="ZScheme.Compiler.Types.EntryPointValidator" /> through the full
///     compilation pipeline: <c>main</c> is the program entry point, so its signature must be
///     one the runtime accepts (≤1 param that, if present, is a CLR string array; an Int/Unit
///     return, or Task&lt;Int&gt;/Task for async).
/// </summary>
public class EntryPointValidatorTests
{
    private static CompilationResult Compile(string source, OutputMode mode = OutputMode.CSharp)
    {
        var options = new CompilerOptions { OutputMode = mode, AllowsImplicitModuleName = true };
        return new Compilation(options).Compile(source);
    }

    private static void AssertEntryPointError(string source, string expectedFragment)
    {
        var result = Compile(source);
        Assert.IsType<CompilationResult.EntryPointValidationFailure>(result);
        Assert.Contains(result.Diagnostics.Diagnostics, d => d.Message.Contains(expectedFragment));
    }

    // ─── Rejected signatures ──────────────────────────────────────────

    [Fact]
    public void TwoParameters_IsRejected()
    {
        AssertEntryPointError(
            "(module test)\n(define (main [a : (Mutable-Vector String)] [b : Int]) : Int 0)",
            "at most one parameter"
        );
    }

    [Fact]
    public void NonArrayListParameter_IsRejected()
    {
        AssertEntryPointError(
            "(module test)\n(define (main [args : (List String)]) : Int 0)",
            "CLR string array"
        );
    }

    [Fact]
    public void NonArrayIntParameter_IsRejected()
    {
        AssertEntryPointError(
            "(module test)\n(define (main [args : Int]) : Int 0)",
            "CLR string array"
        );
    }

    [Fact]
    public void ArrayOfNonString_IsRejected()
    {
        AssertEntryPointError(
            "(module test)\n(define (main [args : (Mutable-Vector Int)]) : Int 0)",
            "CLR string array"
        );
    }

    [Fact]
    public void StringReturn_IsRejected()
    {
        AssertEntryPointError(
            "(module test)\n(define (main) : String \"x\")",
            "must return Int or Unit"
        );
    }

    [Fact]
    public void FloatReturn_IsRejected()
    {
        AssertEntryPointError(
            "(module test)\n(define (main) : Float 1.0)",
            "must return Int or Unit"
        );
    }

    [Fact]
    public void AsyncTaskStringReturn_IsRejected()
    {
        AssertEntryPointError(
            "(module test)\n(define-async (main) : (Task String) \"x\")",
            "async 'main' must return"
        );
    }

    // ─── Accepted signatures ──────────────────────────────────────────

    [Fact]
    public void IntReturn_NoParams_IsExecutable()
    {
        var result = Compile("(module test)\n(define (main) : Int 0)");
        var cs = Assert.IsType<CompilationResult.CSharpOutputResult>(result);
        Assert.True(cs.IsExecutable);
    }

    [Fact]
    public void IntReturn_MutableVectorParam_IsExecutable()
    {
        var result = Compile(
            "(module test)\n(define (main [args : (Mutable-Vector String)]) : Int 0)"
        );
        var cs = Assert.IsType<CompilationResult.CSharpOutputResult>(result);
        Assert.True(cs.IsExecutable);
    }

    [Fact]
    public void ClrArrayParam_IsAccepted()
    {
        var result = Compile("(module test)\n(define (main [args : (Clr-Array String)]) : Int 0)");
        var cs = Assert.IsType<CompilationResult.CSharpOutputResult>(result);
        Assert.True(cs.IsExecutable);
    }

    [Fact]
    public void UnitReturn_IsExecutable_CSharp()
    {
        var result = Compile("(module test)\n(define (main) : Unit ())");
        var cs = Assert.IsType<CompilationResult.CSharpOutputResult>(result);
        Assert.True(cs.IsExecutable);
        // Unit lowers to a void entry point — no exit-code conversion, no wrapper.
        Assert.Contains("static void Main(", cs.CsOutput);
    }

    [Fact]
    public void UnitReturn_IsExecutable_Il()
    {
        var result = Compile("(module test)\n(define (main) : Unit ())", OutputMode.Il);
        var il = Assert.IsType<CompilationResult.IlOutputResult>(result);
        Assert.True(il.IsExecutable);
        Assert.NotNull(il.OutputBytes);
    }

    [Fact]
    public void AsyncTaskInt_IsExecutable_CSharp()
    {
        var result = Compile("(module test)\n(define-async (main) : (Task Int) 0)");
        var cs = Assert.IsType<CompilationResult.CSharpOutputResult>(result);
        Assert.True(cs.IsExecutable);
        // Roslyn discovers `async Task<int> Main` directly as the entry point.
        Assert.Contains("async System.Threading.Tasks.Task<int> Main(", cs.CsOutput);
    }

    [Fact]
    public void AsyncTaskUnit_IsExecutable_CSharp()
    {
        var result = Compile("(module test)\n(define-async (main) : (Task Unit) ())");
        var cs = Assert.IsType<CompilationResult.CSharpOutputResult>(result);
        Assert.True(cs.IsExecutable);
    }

    [Fact]
    public void AsyncTaskInt_IsExecutable_Il()
    {
        // The IL backend emits a synchronous <Main>$ shim that blocks on the async main's Task.
        var result = Compile("(module test)\n(define-async (main) : (Task Int) 0)", OutputMode.Il);
        var il = Assert.IsType<CompilationResult.IlOutputResult>(result);
        Assert.True(il.IsExecutable);
        Assert.NotNull(il.OutputBytes);
    }
}
