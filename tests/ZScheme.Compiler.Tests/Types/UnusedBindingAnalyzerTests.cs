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
        // let is non-recursive: x in the value refers to an outer x, not itself.
        var diag = Analyze("(define (f x) (let ([y y]) 2))");
        var warning = Assert.Single(Unused(diag));
        Assert.Equal(["y"], warning.Data);
    }

    [Fact]
    public void UseInWithHandlersBody_Counts()
    {
        Assert.Empty(
            Unused(
                Analyze(
                    "(define (f) (let ([x 1]) (with-handlers ([Exception e] 0) x)))"
                )
            )
        );
    }

    [Fact]
    public void HandlerVariableShadow_DoesNotCount()
    {
        var diag = Analyze(
            "(define (f) (let ([e 1]) (with-handlers ([Exception e] e) 0)))"
        );
        Assert.Single(Unused(diag));
    }
}
