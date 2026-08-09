using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

public class TailRecursionAnalyzerTests
{
    private static DiagnosticBag Analyze(string source, bool warnUnloopedRecursion = true)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);

        new TailRecursionAnalyzer(diag, warnUnloopedRecursion).Analyze(program);
        return diag;
    }

    private static IEnumerable<Diagnostic> Unlooped(DiagnosticBag diag)
    {
        return diag.Diagnostics.Where(d => d.Code == DiagnosticCodes.NonLoopedSelfRecursion);
    }

    private static Diagnostic Single(string source)
    {
        return Assert.Single(Unlooped(Analyze(source)));
    }

    private static void AssertSilent(string source)
    {
        Assert.Empty(Unlooped(Analyze(source)));
    }

    // ---- Silent: TailCallLowering will loop these ------------------------------------

    [Fact]
    public void TailCall_InIfBranch_IsSilent()
    {
        AssertSilent("(define (loop n acc) (if (= n 0) acc (loop (- n 1) (+ acc n))))");
    }

    [Fact]
    public void TailCall_InLetBody_IsSilent()
    {
        AssertSilent("(define (loop n) (let ([m (- n 1)]) (loop m)))");
    }

    [Fact]
    public void TailCall_InMatchArm_IsSilent()
    {
        AssertSilent(
            """
            (define-union Nat (Zero) (Succ [n : Nat]))
            (define (count x acc) (match x [(Zero) acc] [(Succ n) (count n (+ acc 1))]))
            """
        );
    }

    [Fact]
    public void TailCall_AsLastExprOfMultiBody_IsSilent()
    {
        AssertSilent("(define (loop n) (println \"tick\") (loop (- n 1)))");
    }

    [Fact]
    public void PartiallyLooped_IsSilent()
    {
        // One tail arm is enough to make the function a loop, so the pass marks it
        // IsTcoLoop and ZS0005 must stay quiet — even though the other arm still recurses.
        AssertSilent("(define (f n) (if (= n 0) (f 1) (+ 1 (f (- n 1)))))");
    }

    [Fact]
    public void NonRecursiveFunction_IsSilent()
    {
        AssertSilent("(define (double n) (* n 2))");
    }

    [Fact]
    public void MutualRecursion_IsSilent()
    {
        // Never looped either, but out of scope: the pass only rewrites self-calls.
        AssertSilent(
            """
            (define (even? n) (if (= n 0) #t (odd? (- n 1))))
            (define (odd? n) (if (= n 0) #f (even? (- n 1))))
            """
        );
    }

    [Fact]
    public void ShadowedName_IsNotASelfCall()
    {
        AssertSilent("(define (f n) (let ([f 1]) f))");
    }

    [Fact]
    public void Iife_ThatBetaReductionWillFlatten_IsSilent()
    {
        // IiffeBetaReducer turns this into a `let` spine, putting the call in tail position.
        AssertSilent("(define (f n) (if (= n 0) 0 ((lambda (m) (f m)) (- n 1))))");
    }

    [Fact]
    public void RecursiveMarker_Silences()
    {
        AssertSilent("(define #:recursive (fact n) (if (= n 0) 1 (* n (fact (- n 1)))))");
    }

    [Fact]
    public void RecursiveMarker_Silences_DefineAsync()
    {
        AssertSilent("(define-async #:recursive (poll n) (if (= n 0) 0 (await (poll (- n 1)))))");
    }

    [Fact]
    public void DisabledFlag_Silences()
    {
        var diag = Analyze(
            "(define (fact n) (if (= n 0) 1 (* n (fact (- n 1)))))",
            warnUnloopedRecursion: false
        );
        Assert.Empty(Unlooped(diag));
    }

    // ---- Warns: not tail position ----------------------------------------------------

    [Fact]
    public void NonTailSelfCall_Warns_AtTheNameSpan()
    {
        var warning = Single("(define (fact n) (if (= n 0) 1 (* n (fact (- n 1)))))");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(["fact", "not-tail"], warning.Data);
        Assert.Contains("not in tail position", warning.Message);
        // `fact` is at column 10 (1-based).
        Assert.Equal(10, warning.Span.Column);
        Assert.Equal(4, warning.Span.Length);
    }

    [Fact]
    public void SelfCall_InIfCondition_Warns()
    {
        var warning = Single("(define (f n) (if (= 0 (f (- n 1))) 1 2))");
        Assert.Equal(["f", "not-tail"], warning.Data);
    }

    [Fact]
    public void SelfCall_InMatchScrutinee_Warns()
    {
        var warning = Single(
            """
            (define-union Nat (Zero) (Succ [n : Nat]))
            (define (f x) (match (f x) [(Zero) 0] [(Succ n) 1]))
            """
        );
        Assert.Equal(["f", "not-tail"], warning.Data);
    }

    [Fact]
    public void SelfCall_InLetValue_Warns()
    {
        var warning = Single("(define (f n) (let ([m (f (- n 1))]) m))");
        Assert.Equal(["f", "not-tail"], warning.Data);
    }

    [Fact]
    public void SelfCall_InArgument_Warns()
    {
        // The self-call is an argument to another call, so it never reaches tail position.
        var warning = Single(
            """
            (define (double x) (* x 2))
            (define (f n) (if (= n 0) 0 (double (f (- n 1)))))
            """
        );
        Assert.Equal(["f", "not-tail"], warning.Data);
    }

    // ---- Warns: barred by an enclosing frame ------------------------------------------

    [Fact]
    public void TailCall_InWithHandlersBody_Warns()
    {
        var warning = Single(
            """
            (define (loop n)
              (with-handlers ([System.Exception e] 0)
                (if (= n 0) 0 (loop (- n 1)))))
            """
        );
        Assert.Equal(["loop", "barrier"], warning.Data);
        Assert.Contains("with-handlers", warning.Message);
    }

    [Fact]
    public void TailCall_InUseBody_Warns()
    {
        var warning = Single(
            """
            (define (loop n s)
              (use ([r s])
                (if (= n 0) 0 (loop (- n 1) s))))
            """
        );
        Assert.Equal(["loop", "barrier"], warning.Data);
        Assert.Contains("'use'", warning.Message);
    }

    // ---- Warns: not a top-level define ------------------------------------------------

    [Fact]
    public void LambdaBoundWithDefine_IsSilent()
    {
        // `define` value bindings are non-recursive, so this is an undefined-variable error,
        // not un-looped recursion. Reporting it here would only pile onto a broken program.
        AssertSilent("(define f (lambda (n) (if (= n 0) 0 (f (- n 1)))))");
    }

    [Fact]
    public void NestedDefine_Warns()
    {
        var warning = Single(
            """
            (define (outer x)
              (define (inner n) (if (= n 0) 0 (inner (- n 1))))
              (inner x))
            """
        );
        Assert.Equal(["inner", "not-top-level"], warning.Data);
    }

    // ---- Warns: async ------------------------------------------------------------------

    [Fact]
    public void AsyncTailSelfCall_Warns_AboutTheIlBackend()
    {
        var warning = Single("(define-async (poll n) (if (= n 0) 0 (poll (- n 1))))");
        Assert.Equal(["poll", "async"], warning.Data);
        Assert.Contains("IL backend", warning.Message);
    }

    [Fact]
    public void AsyncAwaitedSelfCall_IsNotTail()
    {
        // `await` is not a tail position on either backend, so this is the ordinary
        // non-tail case rather than the backend-asymmetry one.
        var warning = Single("(define-async (poll n) (if (= n 0) 0 (await (poll (- n 1)))))");
        Assert.Equal(["poll", "not-tail"], warning.Data);
    }
}
