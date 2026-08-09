using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Pipeline;

/// <summary>
///     Pins <see cref="TailRecursionAnalyzer" /> to <see cref="TailCallLowering" />: ZS0005
///     silence must mean the pass marks the function <c>IsTcoLoop</c>, and a warning must mean
///     it does not. The analyzer models the pass's rules one stage earlier, on the AST, so
///     without this test either side could drift and the diagnostic would start lying.
/// </summary>
public class TailRecursionDriftTests
{
    /// <summary>
    ///     Every source defines a self-recursive <c>f</c>. Each row is (source,
    ///     expectedLooped). Only self-recursive functions belong here: the contract is a
    ///     biconditional over functions the pass could plausibly loop, and a non-recursive
    ///     function is neither looped nor warned about.
    /// </summary>
    public static TheoryData<string, bool> Corpus =>
        new()
        {
            // --- looped: a self-call on the tail spine ---
            {
                "(define (f [n : Int] [acc : Int]) : Int (if (= n 0) acc (f (- n 1) (+ acc n))))",
                true
            },
            { "(define (f [n : Int]) : Int (let ([m (- n 1)]) (f m)))", true },
            { "(define (f [n : Int]) : Int n (f (- n 1)))", true },
            {
                """
                    (define-union Nat (Zero) (Succ [n : Nat]))
                    (define (f [x : Nat] [acc : Int]) : Int
                      (match x [(Zero) acc] [(Succ n) (f n (+ acc 1))]))
                    """,
                true
            },
            // One tail arm is enough — the other arm still grows the stack, but the pass
            // marks the function a loop and the analyzer must stay quiet to match.
            { "(define (f [n : Int]) : Int (if (= n 0) (f 1) (+ 1 (f (- n 1)))))", true },
            // The beta-reducer flattens this IIFE into a let spine, putting the call in tail
            // position. The analyzer sees through the same shape.
            { "(define (f [n : Int]) : Int (if (= n 0) 0 ((lambda (m) (f m)) (- n 1))))", true },
            // --- not looped: not in tail position ---
            { "(define (f [n : Int]) : Int (if (= n 0) 1 (* n (f (- n 1)))))", false },
            { "(define (f [n : Int]) : Int (if (= 0 (f (- n 1))) 1 2))", false },
            { "(define (f [n : Int]) : Int (let ([m (f (- n 1))]) m))", false },
            {
                """
                    (define (g [x : Int]) : Int (* x 2))
                    (define (f [n : Int]) : Int (if (= n 0) 0 (g (f (- n 1)))))
                    """,
                false
            },
            {
                """
                    (define-union Nat (Zero) (Succ [n : Nat]))
                    (define (f [x : Nat]) : Nat (match (f x) [(Zero) Zero] [(Succ n) Zero]))
                    """,
                false
            },
            // --- not looped: barred by an enclosing frame ---
            {
                """
                    (define (f [n : Int]) : Int
                      (with-handlers ([System.Exception e] 0) (if (= n 0) 0 (f (- n 1)))))
                    """,
                false
            },
            // --- not looped: not a top-level define ---
            {
                """
                    (define (outer [x : Int]) : Int
                      (define (f [n : Int]) : Int (if (= n 0) 0 (f (- n 1))))
                      (f x))
                    """,
                false
            },
        };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void AnalyzerSilence_MatchesTcoLoop(string source, bool expectedLooped)
    {
        Assert.Equal(expectedLooped, IsLooped(source, "f", includeAsync: true));
        Assert.Equal(expectedLooped, !Warned(source, "f"));
    }

    [Fact]
    public void AsyncTailSelfCall_DoesNotTypeCheck()
    {
        // Why the analyzer's `async` reason has no drift row: an async body's type is the
        // unwrapped result, so a bare tail `(f ...)` yields Task and will not unify with the
        // other branch. Async self-recursion therefore always goes through `await`, which is
        // not a tail position on either backend. If this ever compiles, the `async` reason
        // becomes reachable and needs a drift row of its own.
        var compilation = new Compilation(
            new CompilerOptions { AllowsImplicitModuleName = true, StopAfterTypeInference = true }
        );
        compilation.Compile(
            "(define-async (f [n : Int]) : Task (if (= n 0) (begin ()) (f (- n 1))))",
            "drift.zs"
        );

        Assert.True(compilation.GetDiagnostics().HasErrors);
    }

    [Fact]
    public void AwaitedAsyncSelfCall_IsReportedAsNotTail()
    {
        const string source = """
            (define-async (f [n : Int]) : Task
              (if (= n 0) (begin ()) (await (f (- n 1)))))
            """;

        Assert.False(IsLooped(source, "f", includeAsync: true));
        Assert.True(Warned(source, "f"));
    }

    [Fact]
    public void ShadowedSelfName_IsNotCoveredByTheDriftContract()
    {
        // TailCallLowering matches Var.Name with no scope tracking, so it wrongly rewrites a
        // call to a shadowing local as a back-edge. The analyzer is scope-aware and correctly
        // stays silent. Documented here rather than "fixed" in the analyzer to match the bug.
        const string source = "(define (f n) (let ([f 1]) (g f)))";
        Assert.False(Warned(source, "f"));
    }

    // ---- pipeline halves --------------------------------------------------------------

    /// <summary>The analyzer's view: the real pipeline, stopped after stage 4.8.</summary>
    private static bool Warned(string source, string funcName)
    {
        var compilation = new Compilation(
            new CompilerOptions { AllowsImplicitModuleName = true, StopAfterTypeInference = true }
        );
        compilation.Compile(source, "drift.zs");

        return compilation
            .GetDiagnostics()
            .Diagnostics.Any(d =>
                d.Code == DiagnosticCodes.NonLoopedSelfRecursion && d.Data?[0] == funcName
            );
    }

    /// <summary>TailCallLowering's view: lower to IR, then run the real pass.</summary>
    private static bool IsLooped(string source, string funcName, bool includeAsync)
    {
        var diag = new DiagnosticBag();
        var tokens = new Lexer(source, "drift.zs", diag).Tokenize();
        var sexprs = new SExprParser(tokens, diag).ParseAll();
        var program = new AstBuilder(diag).BuildProgram(sexprs);

        var inferer = new TypeInferer(diag);
        inferer.Infer(program, TypeEnv.CreateRoot());
        inferer.Resolve(program);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics.Where(d => d.IsError)));

        var lowering = new IrLowering(
            diag,
            inferer.OutParamsByAlias,
            enableClosureConversion: true,
            canonicalizer: inferer.Canonicalizer
        );
        var ir = lowering.Lower(program);
        var rewritten = new TailCallLowering(includeAsync).Rewrite(ir);

        return FindFunc(rewritten, funcName)?.IsTcoLoop ?? false;
    }

    private static IrNode.FuncDef? FindFunc(IrNode node, string name)
    {
        switch (node)
        {
            case IrNode.FuncDef func when func.Name == name:
                return func;
            case IrNode.Seq seq:
                return seq.Nodes.Select(n => FindFunc(n, name)).FirstOrDefault(f => f is not null);
            default:
                return null;
        }
    }
}
