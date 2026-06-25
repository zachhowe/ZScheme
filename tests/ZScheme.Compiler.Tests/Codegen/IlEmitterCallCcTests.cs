using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;
using ZScheme.Runtime;

namespace ZScheme.Compiler.Tests.Codegen;

public class IlEmitterCallCcTests
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
        var dir = Path.GetDirectoryName(typeof(IlEmitterCallCcTests).Assembly.Location)!;
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
    public void CallCc_TailPosition_Compiles_AndReturns()
    {
        // Tail-position call/cc with no continuation invocation surrounding context.
        // Exercises the lowering path (ClrCall to Runtime.CallCcTyped<Int, Int>) plus
        // the SaveContinuation runtime contract under Runtime.Run.
        var source =
            @"(module test)
(define (early-exit [n : Int]) : Int
  (call/cc (lambda (k) (if (= n 0) (k 99) n))))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));
        Assert.NotEmpty(bytes);

        var asm = Assembly.Load(bytes);
        Assert.Equal(99, RunInIl<int>(asm, "EarlyExit", 0));
        Assert.Equal(7, RunInIl<int>(asm, "EarlyExit", 7));
    }

    [Fact]
    public void CallCc_LetWrappedNormalReturn_ReplaysFrame()
    {
        // Non-tail call/cc inside a let — ContinuationTransform wraps the let with try/catch
        // and synthesizes a __Frame_* class implementing IFrame. Exercises ClassDecl
        // emission with synthesized frames + Cast nodes for unbox/box.
        var source =
            @"(module test)
(define (use-callcc) : Int
  (let ([t (call/cc (lambda (k) 41))])
    (+ t 1)))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));

        var asm = Assembly.Load(bytes);
        Assert.Equal(42, RunInIl<int>(asm, "UseCallcc"));
    }

    [Fact]
    public void CallCc_LetWrappedContinuationInvoked_AbortsAndResumes()
    {
        // (k 41) throws AbortAndResume which Run() catches and replays through the captured
        // frame, threading 41 → 42. End-to-end exercise of synthesized frame Invoke + Cast.
        var source =
            @"(module test)
(define (use-callcc) : Int
  (let ([t (call/cc (lambda (k) (k 41)))])
    (+ t 1)))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));

        var asm = Assembly.Load(bytes);
        Assert.Equal(42, RunInIl<int>(asm, "UseCallcc"));
    }

    [Fact]
    public void CallCc_NestedAcrossNonTailCall_RunsCorrectly()
    {
        // call/cc result feeds another non-tail call. Ensures multiple frames stack and
        // replay in order.
        var source =
            @"(module test)
(define (double [x : Int]) : Int (* x 2))
(define (use-callcc) : Int
  (let ([t (call/cc (lambda (k) (k 5)))])
    (double (+ t 1))))";
        var (ok, bytes, diags) = Compile(source);
        Assert.True(ok, "IL compilation failed: " + string.Join("\n", diags));

        var asm = Assembly.Load(bytes);
        Assert.Equal(12, RunInIl<int>(asm, "UseCallcc"));
    }
}
