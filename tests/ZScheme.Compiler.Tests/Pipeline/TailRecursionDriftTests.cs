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
            // --- async: an awaited tail self-call is a back-edge, same as a sync one ---
            {
                """
                    (define-async (f [n : Int]) : Task
                      (if (= n 0) (begin ()) (await (f (- n 1)))))
                    """,
                true
            },
            {
                """
                    (define-union Nat (Zero) (Succ [n : Nat]))
                    (define-async (f [x : Nat] [acc : Int]) : (Task Int)
                      (match x [(Zero) acc] [(Succ n) (await (f n (+ acc 1)))]))
                    """,
                true
            },
            // ...but only a *direct* self-call under the await: the awaited result here is
            // consumed by the `+`, so the frame has to survive.
            {
                """
                    (define-async (f [n : Int]) : (Task Int)
                      (if (= n 0) 0 (+ 1 (await (f (- n 1))))))
                    """,
                false
            },
            // async, barred by an enclosing frame
            {
                """
                    (define-async (f [n : Int]) : Task
                      (with-handlers ([System.Exception e] ())
                        (if (= n 0) (begin ()) (await (f (- n 1))))))
                    """,
                false
            },
            // async, not a top-level define
            {
                """
                    (define-async (outer [x : Int]) : Task
                      (define-async (f [n : Int]) : Task
                        (if (= n 0) (begin ()) (await (f (- n 1)))))
                      (await (f x)))
                    """,
                false
            },
        };

    /// <summary>
    ///     The same biconditional for class/object methods, whose self-call the pass loops only
    ///     when the class is sealed. Each source defines a self-recursive method <c>f</c>; the
    ///     row is (source, expectedLooped). As above, only genuinely self-recursive methods
    ///     belong here — a name shadowed into something else is neither looped nor warned about
    ///     and gets its own <c>[Fact]</c>.
    /// </summary>
    public static TheoryData<string, bool> MethodCorpus =>
        new()
        {
            // --- looped: sealed class, self-call on the tail spine ---
            {
                """
                    (define-class C
                      [start : Int]
                      (define (f [n : Int] [acc : Int]) : Int
                        (if (= n 0) acc (f (- n 1) (+ acc 1)))))
                    """,
                true
            },
            {
                """
                    (define-class C
                      [start : Int]
                      (define (f [n : Int]) : Int (let ([m (- n 1)]) (f m))))
                    """,
                true
            },
            // A sibling method on the tail spine is not a self-call, but the method's own tail
            // call still is — the pass loops on the latter.
            {
                """
                    (define-class C
                      [start : Int]
                      (define (g [n : Int]) : Int (* n 2))
                      (define (f [n : Int]) : Int (if (= n 0) (g 1) (f (- n 1)))))
                    """,
                true
            },
            // async: an awaited tail self-call in a method, same as in a function
            {
                """
                    (define-class C
                      [start : Int]
                      (define-async (f [n : Int] [acc : Int]) : (Task Int)
                        (if (= n 0) acc (await (f (- n 1) (+ acc 1))))))
                    """,
                true
            },
            // --- not looped: `#:open`, so the self-call dispatches virtually ---
            {
                """
                    (define-class #:open C
                      [start : Int]
                      (define (f [n : Int] [acc : Int]) : Int
                        (if (= n 0) acc (f (- n 1) (+ acc 1)))))
                    """,
                false
            },
            // --- not looped: sealed, but the call is not in tail position ---
            {
                """
                    (define-class C
                      [start : Int]
                      (define (f [n : Int]) : Int (if (= n 0) 1 (* n (f (- n 1))))))
                    """,
                false
            },
            // --- not looped: sealed and tail, but behind a with-handlers frame ---
            {
                """
                    (define-class C
                      [start : Int]
                      (define (f [n : Int]) : Int
                        (with-handlers ([System.Exception e] 0)
                          (if (= n 0) 0 (f (- n 1))))))
                    """,
                false
            },
        };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void AnalyzerSilence_MatchesTcoLoop(string source, bool expectedLooped)
    {
        Assert.Equal(expectedLooped, IsLooped(source, "f"));
        Assert.Equal(expectedLooped, !Warned(source, "f"));
    }

    [Theory]
    [MemberData(nameof(MethodCorpus))]
    public void AnalyzerSilence_MatchesTcoLoop_ForMethods(string source, bool expectedLooped)
    {
        Assert.Equal(expectedLooped, IsMethodLooped(source, "f"));
        Assert.Equal(expectedLooped, !Warned(source, "f"));
    }

    [Fact]
    public void FieldShadowingAMethodName_IsNotRewrittenToABackEdge()
    {
        // Both emitters resolve a bare name to `this.<Field>` before they consider methods, so
        // the `(f ...)` here is not a call to the method and a back-edge would jump somewhere
        // the source never named. Like the `let`/`match` shadowing cases, this is not
        // self-recursion at all, so both halves stay false rather than pairing off.
        const string source = """
            (define-class C
              [f : (Int -> Int)]
              (define (f [n : Int]) : Int (if (= n 0) 0 (f (- n 1)))))
            """;

        Assert.False(IsMethodLooped(source, "f"));
        Assert.False(Warned(source, "f"));
    }

    [Fact]
    public void RecursiveMarkerOnAMethod_SilencesTheWarning()
    {
        // `#:recursive` is the per-definition opt-out, and an `#:open` class's method has no
        // other way to say "the virtual dispatch is what I meant".
        const string source = """
            (define-class #:open C
              [start : Int]
              (define #:recursive (f [n : Int]) : Int (if (= n 0) 0 (f (- n 1)))))
            """;

        Assert.False(IsMethodLooped(source, "f"));
        Assert.False(Warned(source, "f"));
    }

    [Fact]
    public void AsyncTailSelfCall_DoesNotTypeCheck()
    {
        // Why `(await (self ...))` is the whole story for async TCO, and why TailCallLowering
        // needs an Await case rather than relying on its plain Call case: an async body's type
        // is the unwrapped result, so a bare tail `(f ...)` yields Task and will not unify with
        // the other branch. The awaited spelling is the only one valid source can produce.
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
    public void ShadowedSelfName_IsNotRewrittenToABackEdge()
    {
        // A `let` that rebinds the function's own name: the tail call goes to the local, not
        // to `f`, so the pass must leave it a plain Call. Rewriting it to a back-edge jumped
        // to the top of `f` and never called the bound value — the emitted body returned 0
        // for every input instead of 100 * (n - 1).
        const string source = """
            (define (f [n : Int]) : Int
              (if (= n 0)
                  0
                  (let ([f (lambda ([x : Int]) : Int (* x 100))])
                    (f (- n 1)))))
            """;

        Assert.False(IsLooped(source, "f"));
        Assert.False(Warned(source, "f"));
    }

    [Fact]
    public void MatchArmShadowingSelfName_IsNotRewrittenToABackEdge()
    {
        // Same bug through a `match` binder. This one produced a back-edge that assigned the
        // arm's `(Int -> Int)` value into the `Box`-typed parameter slot — code that does not
        // even compile under the C# backend.
        const string source = """
            (define-union Box (B [v : (Int -> Int)]))
            (define (f [b : Box]) : Int
              (match b
                [(B f) (f 1)]))
            """;

        Assert.False(IsLooped(source, "f"));
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
    private static bool IsLooped(string source, string funcName) =>
        FindFunc(Lower(source), funcName)?.IsTcoLoop ?? false;

    /// <summary>The real pipeline down to IR, with TailCallLowering applied.</summary>
    private static IrNode Lower(string source)
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
        return new TailCallLowering().Rewrite(lowering.Lower(program));
    }

    /// <summary>The same, for a method of the (single) class the source declares.</summary>
    private static bool IsMethodLooped(string source, string methodName) =>
        FindMethod(Lower(source), methodName)?.IsTcoLoop ?? false;

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

    private static IrObjectMethod? FindMethod(IrNode node, string name)
    {
        switch (node)
        {
            case IrNode.ClassDecl cls:
                return cls.Methods.FirstOrDefault(m => m.Name == name);
            case IrNode.Seq seq:
                return seq
                    .Nodes.Select(n => FindMethod(n, name))
                    .FirstOrDefault(m => m is not null);
            default:
                return null;
        }
    }
}
