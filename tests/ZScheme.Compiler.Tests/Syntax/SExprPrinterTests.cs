using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Syntax;

public class SExprPrinterTests
{
    private static SExpr ParseOne(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return Assert.Single(sexprs);
    }

    [Fact]
    public void ShortForm_PrintsFlat()
    {
        var result = SExprPrinter.Print(ParseOne("(if a (begin 1) 2)"));
        Assert.Equal("(if a (begin 1) 2)", result.Text);
        Assert.Null(result.MarkedSpan);
    }

    [Fact]
    public void WideForm_BreaksWithIndent()
    {
        var expr = ParseOne("(define (my-function arg-one arg-two) (body-call arg-one arg-two))");
        var result = SExprPrinter.Print(expr, null, 40);
        Assert.Equal(
            "(define\n  (my-function arg-one arg-two)\n  (body-call arg-one arg-two))",
            result.Text
        );
    }

    [Fact]
    public void NestedWideForm_IndentsRelativeToParent()
    {
        var expr = ParseOne("(a (b c-is-quite-long-here d-also-long e-more-padding) f)");
        var result = SExprPrinter.Print(expr, null, 30);
        var lines = result.Text.Split('\n');
        // Outer breaks; inner list starts at column 2 and itself breaks at column 4
        Assert.Equal("(a", lines[0]);
        Assert.StartsWith("  (b", lines[1]);
        Assert.StartsWith("    ", lines[2]);
    }

    [Fact]
    public void MarkedPath_ReportsSpanOfSubtree()
    {
        var expr = ParseOne("(define (f x) (my-if #t 1 2))");
        var result = SExprPrinter.Print(expr, [2]);
        Assert.NotNull(result.MarkedSpan);
        var span = result.MarkedSpan.Value;
        Assert.Equal("(my-if #t 1 2)", result.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void MarkedRoot_CoversWholeText()
    {
        var expr = ParseOne("(if a b c)");
        var result = SExprPrinter.Print(expr, []);
        Assert.NotNull(result.MarkedSpan);
        var span = result.MarkedSpan.Value;
        Assert.Equal(0, span.Start);
        Assert.Equal(result.Text.Length, span.Length);
    }

    [Fact]
    public void MarkedPath_InsideBrokenLayout_MatchesSubtreePrint()
    {
        var expr = ParseOne("(define (my-function arg-one arg-two) (body-call arg-one arg-two))");
        var result = SExprPrinter.Print(expr, [2], 40);
        Assert.NotNull(result.MarkedSpan);
        var span = result.MarkedSpan.Value;
        Assert.Equal("(body-call arg-one arg-two)", result.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void MarkedPath_IntoBracketList()
    {
        var expr = ParseOne("(let* ([x 42]) x)");
        var result = SExprPrinter.Print(expr, [1, 0, 1]);
        Assert.NotNull(result.MarkedSpan);
        var span = result.MarkedSpan.Value;
        Assert.Equal("42", result.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void MarkedAtom_InFlatRegion()
    {
        var expr = ParseOne("(+ 1 2)");
        var result = SExprPrinter.Print(expr, [2]);
        Assert.NotNull(result.MarkedSpan);
        var span = result.MarkedSpan.Value;
        Assert.Equal("2", result.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void StringLiterals_AreRequotedAndEscaped()
    {
        var expr = ParseOne("(print \"a \\\"b\\\" c\\n\")");
        var result = SExprPrinter.Print(expr);
        Assert.Equal("(print \"a \\\"b\\\" c\\n\")", result.Text);
    }

    [Fact]
    public void BracketLists_PrintWithBrackets()
    {
        var expr = ParseOne("(match x [1 \"one\"] [_ \"other\"])");
        var result = SExprPrinter.Print(expr);
        Assert.Equal("(match x [1 \"one\"] [_ \"other\"])", result.Text);
    }

    [Fact]
    public void SharedNodeInstances_MarkOnlyThePathedOccurrence()
    {
        // Simulate a macro binding substituted into two holes: same SExpr instance twice
        var shared = ParseOne("(f 1)");
        var span = SourceSpan.None;
        var list = new SExpr.SList(
            [new SExpr.Atom(new Token(TokenKind.Symbol, "pair", span)), shared, shared],
            span
        );
        var result = SExprPrinter.Print(list, [2]);
        Assert.NotNull(result.MarkedSpan);
        var marked = result.MarkedSpan.Value;
        Assert.Equal("(pair (f 1) (f 1))", result.Text);
        // The second occurrence, not the first
        Assert.Equal(12, marked.Start);
        Assert.Equal("(f 1)", result.Text.Substring(marked.Start, marked.Length));
    }

    [Fact]
    public void RulePrinter_RendersPatternAndTemplate()
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(
            @"(define-syntax my-cond
                (syntax-rules (else)
                  [(my-cond [else body ...]) (begin body ...)]
                  [(my-cond [test body ...] rest ...)
                   (if test (begin body ...) (my-cond rest ...))]))",
            "test.zs",
            diag
        );
        var parser = new SExprParser(lexer.Tokenize(), diag);
        var sexprs = parser.ParseAll();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        var def = new MacroParser(diag).Parse((SExpr.SList)sexprs[0]);
        Assert.NotNull(def);

        Assert.Equal(
            "(my-cond [else body ...]) => (begin body ...)",
            MacroRulePrinter.Print(def.Rules[0])
        );
        Assert.Equal(
            "(my-cond [test body ...] rest ...) => (if test (begin body ...) (my-cond rest ...))",
            MacroRulePrinter.Print(def.Rules[1])
        );
    }
}
