using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Syntax;

public class MacroTraceTests
{
    private static List<SExpr> Parse(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return sexprs;
    }

    private static (List<SExpr> Result, MacroExpansionTrace Trace, DiagnosticBag Diag) Expand(
        string source
    )
    {
        var diag = new DiagnosticBag();
        var sexprs = Parse(source);
        var trace = new MacroExpansionTrace();
        var expander = new MacroExpander(diag, trace);
        var result = expander.ExpandAll(sexprs, new MacroEnvironment());
        return (result, trace, diag);
    }

    private static (List<SExpr> Result, MacroExpansionTrace Trace) ExpandOk(string source)
    {
        var (result, trace, diag) = Expand(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return (result, trace);
    }

    [Fact]
    public void SimpleRewrite_RecordsSingleRootStep()
    {
        var (result, trace) = ExpandOk(
            @"
            (define-syntax my-if
              (syntax-rules ()
                [(my-if c t e) (if c t e)]))
            (my-if #t 1 2)"
        );

        Assert.Single(result);
        var step = Assert.Single(trace.Steps);
        Assert.Equal(0, step.Index);
        Assert.Equal("my-if", step.Macro.Name);
        Assert.Equal(0, step.RuleIndex);
        Assert.Equal(0, step.Depth);
        Assert.Empty(step.PathFromRoot);
        Assert.Equal("(my-if #t 1 2)", step.FormBefore.ToString());
        Assert.Equal("(if #t 1 2)", step.FormAfter.ToString());
        Assert.Equal(step.Redex.ToString(), step.FormBefore.ToString());
        Assert.Equal(step.Expansion.ToString(), step.FormAfter.ToString());
    }

    [Fact]
    public void NestedRedex_SnapshotsWholeTopLevelForm()
    {
        var (_, trace) = ExpandOk(
            @"
            (define-syntax my-if
              (syntax-rules ()
                [(my-if c t e) (if c t e)]))
            (define (f) (my-if #t 1 2))"
        );

        var step = Assert.Single(trace.Steps);
        Assert.Equal([2], step.PathFromRoot);
        Assert.Equal("(define (f) (my-if #t 1 2))", step.FormBefore.ToString());
        Assert.Equal("(define (f) (if #t 1 2))", step.FormAfter.ToString());
        Assert.Equal("(my-if #t 1 2)", step.Redex.ToString());
    }

    [Fact]
    public void SamePositionChain_ChainsFormSnapshots()
    {
        var (result, trace) = ExpandOk(
            @"
            (define-syntax |>
              (syntax-rules ()
                [(|> x) x]
                [(|> x (f args ...) rest ...) (|> (f x args ...) rest ...)]
                [(|> x f rest ...) (|> (f x) rest ...)]))
            (|> 5 (add 1) (mul 2))"
        );

        Assert.Single(result);
        Assert.Equal("(mul (add 5 1) 2)", result[0].ToString());

        Assert.Equal(3, trace.Steps.Count);
        Assert.All(trace.Steps, s => Assert.Empty(s.PathFromRoot));
        Assert.Equal([0, 1, 2], trace.Steps.Select(s => s.Depth));
        Assert.Equal([1, 1, 0], trace.Steps.Select(s => s.RuleIndex));

        for (var i = 0; i + 1 < trace.Steps.Count; i++)
            Assert.Equal(
                trace.Steps[i].FormAfter.ToString(),
                trace.Steps[i + 1].FormBefore.ToString()
            );
    }

    [Fact]
    public void RecursiveCond_ShowsExpandedOuterContext()
    {
        var (result, trace) = ExpandOk(
            @"
            (define-syntax my-cond
              (syntax-rules (else)
                [(my-cond [else body ...]) (begin body ...)]
                [(my-cond [test body ...] rest ...)
                 (if test (begin body ...) (my-cond rest ...))]))
            (my-cond [a 1] [b 2] [else 3])"
        );

        Assert.Single(result);
        Assert.Equal(
            "(if a (begin 1) (if b (begin 2) (begin 3)))",
            result[0].ToString()
        );

        Assert.Equal(3, trace.Steps.Count);
        Assert.Equal([1, 1, 0], trace.Steps.Select(s => s.RuleIndex));

        // The inner cond redexes sit inside the already-expanded outer ifs
        Assert.Equal([], trace.Steps[0].PathFromRoot);
        Assert.Equal([3], trace.Steps[1].PathFromRoot);
        Assert.Equal([3, 3], trace.Steps[2].PathFromRoot);
        Assert.Equal(
            "(if a (begin 1) (my-cond [b 2] [else 3]))",
            trace.Steps[1].FormBefore.ToString()
        );
        Assert.Equal(
            "(if a (begin 1) (if b (begin 2) (my-cond [else 3])))",
            trace.Steps[2].FormBefore.ToString()
        );
        Assert.Equal(
            "(if a (begin 1) (if b (begin 2) (begin 3)))",
            trace.Steps[2].FormAfter.ToString()
        );

        for (var i = 0; i + 1 < trace.Steps.Count; i++)
            Assert.Equal(
                trace.Steps[i].FormAfter.ToString(),
                trace.Steps[i + 1].FormBefore.ToString()
            );
    }

    [Fact]
    public void EllipsisExpansion_SnapshotsInstantiatedTemplate()
    {
        var (_, trace) = ExpandOk(
            @"
            (define-syntax when
              (syntax-rules ()
                [(when cond body ...) (if cond (begin body ...) unit)]))
            (when #t 1 2 3)"
        );

        var step = Assert.Single(trace.Steps);
        Assert.Equal("(when #t 1 2 3)", step.FormBefore.ToString());
        Assert.Equal("(if #t (begin 1 2 3) unit)", step.FormAfter.ToString());
    }

    [Fact]
    public void TwoIndependentCalls_ShowProgressiveSiblings()
    {
        var (result, trace) = ExpandOk(
            @"
            (define-syntax my-id
              (syntax-rules ()
                [(my-id x) (id x)]))
            (pair (my-id 1) (my-id 2))"
        );

        Assert.Single(result);
        Assert.Equal("(pair (id 1) (id 2))", result[0].ToString());

        Assert.Equal(2, trace.Steps.Count);
        var left = trace.Steps[0];
        var right = trace.Steps[1];

        Assert.Equal([1], left.PathFromRoot);
        Assert.Equal([2], right.PathFromRoot);

        // Left step: right sibling still unexpanded
        Assert.Equal("(pair (my-id 1) (my-id 2))", left.FormBefore.ToString());
        Assert.Equal("(pair (id 1) (my-id 2))", left.FormAfter.ToString());

        // Right step: left sibling already expanded
        Assert.Equal("(pair (id 1) (my-id 2))", right.FormBefore.ToString());
        Assert.Equal("(pair (id 1) (id 2))", right.FormAfter.ToString());
    }

    [Fact]
    public void MacroInsideBracketList_RecordsBracketPath()
    {
        var (_, trace) = ExpandOk(
            @"
            (define-syntax my-id
              (syntax-rules ()
                [(my-id x) x]))
            (let* ([x (my-id 42)]) x)"
        );

        var step = Assert.Single(trace.Steps);
        Assert.Equal([1, 0, 1], step.PathFromRoot);
        Assert.Equal("(let* ([x (my-id 42)]) x)", step.FormBefore.ToString());
        Assert.Equal("(let* ([x 42]) x)", step.FormAfter.ToString());
    }

    [Fact]
    public void TopLevelBeginSplicing_SnapshotsPreSplice()
    {
        var (result, trace) = ExpandOk(
            @"
            (define-syntax defs
              (syntax-rules ()
                [(defs a b) (begin (define a 1) (define b 2))]))
            (defs x y)
            (other-form)"
        );

        // Post-splice output: two defines + the untouched form
        Assert.Equal(3, result.Count);
        Assert.Equal("(define x 1)", result[0].ToString());
        Assert.Equal("(define y 2)", result[1].ToString());

        var step = Assert.Single(trace.Steps);
        Assert.Equal("(begin (define x 1) (define y 2))", step.FormAfter.ToString());
        // Input list: [define-syntax, (defs x y), (other-form)]
        Assert.Equal(1, step.TopLevelFormIndex);
    }

    [Fact]
    public void TopLevelFormIndex_TracksInputPositions()
    {
        var (_, trace) = ExpandOk(
            @"
            (define-syntax my-id
              (syntax-rules ()
                [(my-id x) x]))
            (first-form)
            (my-id 1)
            (my-id 2)"
        );

        Assert.Equal(2, trace.Steps.Count);
        Assert.Equal(2, trace.Steps[0].TopLevelFormIndex);
        Assert.Equal(3, trace.Steps[1].TopLevelFormIndex);
    }

    [Fact]
    public void DepthLimit_CollectsStepsAndFlagsLimit()
    {
        var (_, trace, diag) = Expand(
            @"
            (define-syntax loop
              (syntax-rules ()
                [(loop) (loop)]))
            (loop)"
        );

        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("depth limit"));
        Assert.True(trace.DepthLimitHit);
        Assert.True(trace.Steps.Count >= 100);
        Assert.All(trace.Steps, s => Assert.Equal("loop", s.Macro.Name));
    }

    [Fact]
    public void NoMacros_ProducesNoSteps()
    {
        var (result, trace) = ExpandOk(
            @"
            (define-syntax unused
              (syntax-rules ()
                [(unused x) x]))
            (+ 1 2)"
        );

        Assert.Single(result);
        Assert.Empty(trace.Steps);
        Assert.False(trace.DepthLimitHit);
    }

    [Fact]
    public void NoMatchingRule_ProducesNoStepAndDiagnostic()
    {
        var (_, trace, diag) = Expand(
            @"
            (define-syntax two-args
              (syntax-rules ()
                [(two-args a b) (pair a b)]))
            (two-args 1)"
        );

        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("No matching rule"));
        Assert.Empty(trace.Steps);
    }

    [Fact]
    public void NestedMacroExpansion_OuterStepShowsUnexpandedTemplate()
    {
        var (_, trace) = ExpandOk(
            @"
            (define-syntax swap
              (syntax-rules ()
                [(swap a b) (list b a)]))
            (define-syntax double-swap
              (syntax-rules ()
                [(double-swap a b) (swap (swap a b) (swap b a))]))
            (double-swap x y)"
        );

        // Outer macro fires first, then the re-expansion walks its output
        Assert.Equal(4, trace.Steps.Count);
        Assert.Equal("double-swap", trace.Steps[0].Macro.Name);
        Assert.Equal("(swap (swap x y) (swap y x))", trace.Steps[0].FormAfter.ToString());
        Assert.All(trace.Steps.Skip(1), s => Assert.Equal("swap", s.Macro.Name));

        for (var i = 0; i + 1 < trace.Steps.Count; i++)
            Assert.Equal(
                trace.Steps[i].FormAfter.ToString(),
                trace.Steps[i + 1].FormBefore.ToString()
            );
    }
}
