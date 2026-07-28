using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

public class UnusedBindingAnalyzerTests
{
    private static DiagnosticBag Analyze(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);

        new UnusedBindingAnalyzer(diag).Analyze(program);
        return diag;
    }

    private static IEnumerable<Diagnostic> Unused(DiagnosticBag diag)
    {
        return diag.Diagnostics.Where(d => d.Code == DiagnosticCodes.UnusedBinding);
    }

    [Fact]
    public void UnusedLet_Warns_AtTheNameSpan()
    {
        var diag = Analyze("(define (f) (let ([x 1]) 2))");

        var warning = Assert.Single(Unused(diag));
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(["x"], warning.Data);
        // `x` is at column 20 (1-based).
        Assert.Equal(20, warning.Span.Column);
        Assert.Equal(1, warning.Span.Length);
    }

    [Fact]
    public void UsedLet_DoesNotWarn()
    {
        Assert.Empty(Unused(Analyze("(define (f) (let ([x 1]) x))")));
    }

    [Fact]
    public void UnusedLetrec_Warns_AtTheNameSpan()
    {
        var diag = Analyze("(define (f) (letrec ([x 1]) 2))");

        var warning = Assert.Single(Unused(diag));
        Assert.Equal(["x"], warning.Data);
        // `x` is at column 23 (1-based).
        Assert.Equal(23, warning.Span.Column);
    }

    [Fact]
    public void UsedLetrec_DoesNotWarn()
    {
        Assert.Empty(Unused(Analyze("(define (f) (letrec ([x 1]) x))")));
    }

    [Fact]
    public void LetrecBindingUsedByASibling_DoesNotWarn()
    {
        // `a` is never named in the body, but `f` reads it — the group's own values are part
        // of each binding's scope, so that counts as use.
        Assert.Empty(
            Unused(Analyze("(define (f) (letrec ([a 1] [g (lambda (n) (+ n a))]) (g 0)))"))
        );
    }

    [Fact]
    public void LetrecSelfReferenceAlone_IsNotUse()
    {
        // A function that only ever calls itself is dead, exactly like an unreferenced
        // self-recursive private define.
        var diag = Analyze("(define (f) (letrec ([g (lambda (n) (g n))]) 2))");

        var warning = Assert.Single(Unused(diag));
        Assert.Equal(["g"], warning.Data);
    }

    [Fact]
    public void ShadowedByLetrec_OuterIsUnused()
    {
        var diag = Analyze("(define (f) (let ([x 1]) (letrec ([x 2]) x)))");

        var warning = Assert.Single(Unused(diag));
        // The OUTER x (col 20) is shadowed by the letrec binding, so it goes unused.
        Assert.Equal(20, warning.Span.Column);
    }

    [Fact]
    public void UnderscoreBindings_AreExempt()
    {
        Assert.Empty(Unused(Analyze("(define (f) (let ([_ 1]) 2))")));
        Assert.Empty(Unused(Analyze("(define (f) (let ([_x 1]) 2))")));
    }

    [Fact]
    public void MultiBodyLet_DesugaredWrappers_AreExempt()
    {
        // The extra body expressions desugar to Let("_", ...) with no NameSpan.
        Assert.Empty(Unused(Analyze("(define (f) (let ([x 1]) (g x) (h x)))")));
    }

    [Fact]
    public void ShadowedByInnerLet_OuterIsUnused()
    {
        var diag = Analyze("(define (f) (let ([x 1]) (let ([x 2]) x)))");

        var warning = Assert.Single(Unused(diag));
        // The OUTER x (col 20) is unused; the inner one (col 33) is used.
        Assert.Equal(20, warning.Span.Column);
    }

    [Fact]
    public void UseInInnerValue_Counts()
    {
        // Inner let shadows x in its body, but its VALUE still references outer x.
        Assert.Empty(Unused(Analyze("(define (f) (let ([x 1]) (let ([x x]) x)))")));
    }

    [Fact]
    public void LambdaParameterShadow_DoesNotCount()
    {
        var diag = Analyze("(define (f) (let ([x 1]) (lambda ([x : Int]) x)))");
        Assert.Single(Unused(diag));
    }

    [Fact]
    public void MatchPatternVariableShadow_DoesNotCount()
    {
        var diag = Analyze("(define (f y) (let ([x 1]) (match y [x x])))");
        var warning = Assert.Single(Unused(diag));
        Assert.Equal(["x"], warning.Data);
        Assert.Equal(22, warning.Span.Column); // the let's x, not the pattern's
    }

    [Fact]
    public void MatchScrutineeUse_Counts()
    {
        Assert.Empty(Unused(Analyze("(define (f) (let ([x 1]) (match x [_ 0])))")));
    }

    [Fact]
    public void UnusedUse_Warns_WithDisposalMessage()
    {
        var diag = Analyze("(define (f s) (use ([r s]) 2))");

        var warning = Assert.Single(Unused(diag));
        Assert.Contains("disposed", warning.Message);
    }

    [Fact]
    public void LetStar_MiddleUnusedBinding_Warns()
    {
        var diag = Analyze("(define (f) (let* ([a 1] [b 2] [c a]) c))");

        var warning = Assert.Single(Unused(diag));
        Assert.Equal(["b"], warning.Data);
    }

    [Fact]
    public void LetValueIsOutsideTheScope_SelfReferenceDoesNotCount()
    {
        // let is non-recursive: y in the value refers to the parameter, not itself —
        // the parameter counts as used, only the let binding is flagged.
        var diag = Analyze("(define (f y) (let ([y y]) 2))");
        var warning = Assert.Single(Unused(diag));
        Assert.Equal(["y"], warning.Data);
    }

    [Fact]
    public void UseInWithHandlersBody_Counts()
    {
        Assert.Empty(
            Unused(Analyze("(define (f) (let ([x 1]) (with-handlers ([Exception e] 0) x)))"))
        );
    }

    [Fact]
    public void HandlerVariableShadow_DoesNotCount()
    {
        var diag = Analyze("(define (f) (let ([e 1]) (with-handlers ([Exception e] e) 0)))");
        Assert.Single(Unused(diag));
    }

    [Fact]
    public void UnusedParameter_Warns_AtTheNameSpan()
    {
        var diag = Analyze("(define (f [count : Int]) : Int 1)");

        var warning = Assert.Single(Unused(diag));
        Assert.Contains("parameter", warning.Message);
        Assert.Equal(["count"], warning.Data);
        // The warning points at the name atom, not the [count : Int] bracket.
        Assert.Equal("count".Length, warning.Span.Length);
    }

    [Fact]
    public void UnusedParameter_UnderscorePrefix_OptsOut()
    {
        Assert.Empty(Unused(Analyze("(define (f [_count : Int]) : Int 1)")));
    }

    [Fact]
    public void UnusedLambdaAndMethodParameters_Warn()
    {
        Assert.Single(Unused(Analyze("(define (f) (lambda ([x : Int]) 1))")));
        Assert.Single(Unused(Analyze("(define-class C (define (M [x : Int]) : Int 1))")));
    }

    [Fact]
    public void UnusedParameter_ToggleOff_Silences()
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer("(define (f [count : Int]) : Int 1)", "test.zs", diag);
        var parser = new SExprParser(lexer.Tokenize(), diag);
        var program = new AstBuilder(diag).BuildProgram(parser.ParseAll());

        new UnusedBindingAnalyzer(diag, warnUnusedParameters: false).Analyze(program);

        Assert.Empty(Unused(diag));
    }

    [Fact]
    public void UnusedPrivateDefine_Warns_OnlyWhenProgramExports()
    {
        // Without an (export ...) form, "private" is meaningless: stay silent.
        Assert.Empty(Unused(Analyze("(define (helper) 1)\n(define (main) 0)")));

        var diag = Analyze(
            """
            (module m)
            (define (used-helper) 1)
            (define (pub) (used-helper))
            (define (dead-helper) 2)
            (export pub)
            """
        );
        var warning = Assert.Single(Unused(diag));
        Assert.Contains("private definition", warning.Message);
        Assert.Equal(["dead-helper"], warning.Data);
    }

    [Fact]
    public void UnusedPrivateDefine_SelfRecursionDoesNotCountAsUse()
    {
        var diag = Analyze(
            """
            (module m)
            (define (loop [n : Int]) : Int (loop n))
            (define (pub) 1)
            (export pub)
            """
        );
        var warning = Assert.Single(Unused(diag));
        Assert.Equal(["loop"], warning.Data);
    }

    [Fact]
    public void ExportedMainAndUnderscoreDefines_AreExempt()
    {
        Assert.Empty(
            Unused(
                Analyze(
                    """
                    (module m)
                    (define (pub) 1)
                    (define (main) 0)
                    (define (_scratch) 2)
                    (export pub)
                    """
                )
            )
        );
    }
}
