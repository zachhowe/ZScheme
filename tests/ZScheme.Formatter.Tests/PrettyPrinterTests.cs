using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Formatter;

namespace ZScheme.Formatter.Tests;

public class PrettyPrinterTests
{
    [Fact]
    public void SingleImport_FormattedCorrectly()
    {
        Assert.Equal("(import a)\n", Format("(import a)"));
    }

    [Fact]
    public void MultipleImports_MergedOntoOneForm()
    {
        var result = Format("(import a)\n(import b)\n(import c)");
        Assert.Equal("(import a\n        b\n        c)\n", result);
    }

    [Fact]
    public void MultiModuleImport_BreaksAlignedOnePerLine()
    {
        var result = Format("(import stdlib/list stdlib/map stdlib/option)");
        Assert.Equal("(import stdlib/list\n        stdlib/map\n        stdlib/option)\n", result);
    }

    [Fact]
    public void FunctionBody_BreaksOntoOwnLine()
    {
        var result = Format("(define (square [x : Int]) : Int (* x x))");
        Assert.Equal("(define (square [x : Int]) : Int\n    (* x x))\n", result);
    }

    [Fact]
    public void ShortInlineForm_StaysOnOneLine()
    {
        // `if` is width-based: it fits, so it is not exploded.
        var result = Format("(define (f [x : Int]) : Int (if (> x 0) x 0))");
        Assert.Contains("(if (> x 0) x 0)", result);
    }

    [Fact]
    public void DefineRecord_StaysInlineWhenItFits()
    {
        Assert.Equal(
            "(define-record Point [x : Int] [y : Int])\n",
            Format("(define-record Point [x : Int] [y : Int])")
        );
    }

    [Fact]
    public void Match_ArmsAreStackedEvenWhenItWouldFit()
    {
        var result = Format("(define (d [n : Int]) : Int (match n [0 0] [_ 1]))");
        Assert.Contains("(match n\n", result);
        Assert.Contains("    [0 0]\n", result);
        Assert.Contains("    [_ 1]", result);
    }

    [Fact]
    public void LetBindings_KeepTheirBrackets()
    {
        var result = Format("(define (f) (let [[x 1] [y 2]] (+ x y)))");
        Assert.Contains("[[x 1] [y 2]]", result);
    }

    [Fact]
    public void LetStarBindings_AlignUnderTheFirstBindingWhenBroken()
    {
        var narrow = FormattingOptions.Default with { MaxLineLength = 40 };
        var result = Format(
            "(define (f)\n  (let* ([doubled (* n 2)] [incremented (+ doubled 1)] [squared (* i i)])\n    squared))",
            narrow
        );
        // Each binding on its own line, aligned under the first binding's opening bracket
        // (one column past the "(let* (" head), and the body one indent level under `let*`.
        Assert.Contains(
            "    (let* ([doubled (* n 2)]\n"
                + "           [incremented (+ doubled 1)]\n"
                + "           [squared (* i i)])\n"
                + "        squared))",
            result
        );
    }

    [Fact]
    public void LetBindings_StayFlatWhenTheyFit()
    {
        var result = Format("(define (f) (let ([x 1] [y 2]) (+ x y)))");
        Assert.Contains("(let ([x 1] [y 2])", result);
    }

    [Fact]
    public void UseStarBindings_AlignUnderTheFirstBindingWhenBroken()
    {
        var narrow = FormattingOptions.Default with { MaxLineLength = 45 };
        var result = Format(
            "(define (f a b)\n  (use* ([x (acquire a)] [y (acquire b)] [z (acquire-more a b)])\n    (process x y z)))",
            narrow
        );
        Assert.Contains(
            "    (use* ([x (acquire a)]\n"
                + "           [y (acquire b)]\n"
                + "           [z (acquire-more a b)])\n"
                + "        (process x y z)))",
            result
        );
    }

    [Fact]
    public void UseForm_StaysFlatWhenItFits()
    {
        // Unlike let, use/use* are not force-broken: a short resource scope stays inline.
        var result = Format("(define (f) (use ([m (new Stream)]) m))");
        Assert.Contains("(use ([m (new Stream)]) m)", result);
    }

    [Fact]
    public void Lambda_StaysInlineAsAnArgument()
    {
        var result = Format("(map (lambda (x) (* x 2)) lst)");
        Assert.Equal("(map (lambda (x) (* x 2)) lst)\n", result);
    }

    [Theory]
    [InlineData("(define s \"hello world\")", "(define s \"hello world\")\n")]
    [InlineData("(define s \"a\\nb\")", "(define s \"a\\nb\")\n")]
    [InlineData("(define s \"tab\\there\")", "(define s \"tab\\there\")\n")]
    [InlineData("(define s \"q\\\"q\")", "(define s \"q\\\"q\")\n")]
    [InlineData("(define s \"back\\\\slash\")", "(define s \"back\\\\slash\")\n")]
    public void StringLiterals_AreReQuotedAndEscaped(string source, string expected)
    {
        Assert.Equal(expected, Format(source));
    }

    [Fact]
    public void QuoteSugar_IsPreserved()
    {
        Assert.Equal("(define x 'sym)\n", Format("(define x 'sym)"));
    }

    [Fact]
    public void LeadingComment_StaysAboveTheForm()
    {
        Assert.Equal(";; header\n(import a)\n", Format(";; header\n(import a)"));
    }

    [Fact]
    public void BlankLineBetweenLeadingComments_IsPreserved()
    {
        Assert.Equal(";; a\n\n;; b\n(define x 1)\n", Format(";; a\n\n;; b\n(define x 1)"));
    }

    [Fact]
    public void BlankLineBetweenCommentAndForm_IsPreserved()
    {
        Assert.Equal(";; a\n\n(define x 1)\n", Format(";; a\n\n(define x 1)"));
    }

    [Fact]
    public void MultipleBlankLinesBetweenComments_CollapseToOne()
    {
        Assert.Equal(";; a\n\n;; b\n(define x 1)\n", Format(";; a\n\n\n\n;; b\n(define x 1)"));
    }

    [Fact]
    public void InlineAndTrailingComments_StayInPlace()
    {
        var result = Format("(define (f x)\n  ;; inner\n  (+ x 1)) ; trailing\n");
        Assert.Equal("(define (f x)\n    ;; inner\n    (+ x 1))  ; trailing\n", result);
    }

    [Fact]
    public void BlankLineBetweenTopLevelForms_IsPreserved()
    {
        Assert.Equal("(define x 1)\n\n(define y 2)\n", Format("(define x 1)\n\n(define y 2)"));
    }

    [Fact]
    public void MultipleBlankLines_CollapseToOne()
    {
        Assert.Equal("(define x 1)\n\n(define y 2)\n", Format("(define x 1)\n\n\n\n(define y 2)"));
    }

    [Fact]
    public void NoBlankLine_IsNotInvented()
    {
        Assert.Equal("(define x 1)\n(define y 2)\n", Format("(define x 1)\n(define y 2)"));
    }

    [Theory]
    [InlineData("(define (square [x : Int]) : Int (* x x))")]
    [InlineData("(define (d [n : Int]) : Int (match n [0 0] [_ 1]))")]
    [InlineData(";; c\n(define x 1)\n\n(define y 2) ; t\n")]
    [InlineData("(define (f) (let [[x 1] [y 2]] (+ x y)))")]
    [InlineData(
        "(define (f)\n  (let* ([doubled (* n 2)]\n         [incremented (+ doubled 1)]\n         [squared (* i i)])\n    squared))"
    )]
    [InlineData(";; a\n\n;; b\n(define x 1)\n")]
    [InlineData(";; a\n\n(define x 1)\n")]
    [InlineData("(import stdlib/list stdlib/map stdlib/option)")]
    public void Formatting_IsIdempotent(string source)
    {
        var once = Format(source);
        var twice = Format(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void MaxLineLength_SmallValueForcesBreakThatDefaultKeepsFlat()
    {
        const string source = "(foobar arg1 arg2)";
        Assert.Equal("(foobar arg1 arg2)\n", Format(source));

        var narrow = FormattingOptions.Default with { MaxLineLength = 15 };
        Assert.Equal("(foobar arg1\n    arg2)\n", Format(source, narrow));
    }

    [Fact]
    public void TrailingCommentSpaces_IsConfigurable()
    {
        const string source = "(define x 1) ; c";
        Assert.Equal("(define x 1)  ; c\n", Format(source)); // default: two spaces

        var oneSpace = FormattingOptions.Default with { TrailingCommentSpaces = 1 };
        Assert.Equal("(define x 1) ; c\n", Format(source, oneSpace));
    }

    [Fact]
    public void AlwaysBreakBody_RemovingMatch_LetsItStayInlineWhenItFits()
    {
        const string source = "(define (d [n : Int]) : Int (match n [0 0] [_ 1]))";
        Assert.Contains("(match n\n", Format(source)); // default: match arms are stacked

        var without = new HashSet<string>(FormattingOptions.DefaultAlwaysBreakBody);
        without.Remove("match");
        var options = FormattingOptions.Default with { AlwaysBreakBody = without };
        Assert.Contains("(match n [0 0] [_ 1])", Format(source, options)); // now width-based -> inline
    }

    [Fact]
    public void AlwaysBreakBody_AddingKeyword_ForcesAnOtherwiseFlatFormToBreak()
    {
        const string source = "(foo a b)";
        Assert.Equal("(foo a b)\n", Format(source)); // default: a plain call stays flat

        var with = new HashSet<string>(FormattingOptions.DefaultAlwaysBreakBody) { "foo" };
        var options = FormattingOptions.Default with { AlwaysBreakBody = with };
        Assert.Equal("(foo\n    a\n    b)\n", Format(source, options));
    }

    private static string Format(string source) => Format(source, FormattingOptions.Default);

    private static string Format(string source, FormattingOptions options)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var (tokens, comments) = lexer.TokenizeWithComments();
        var parser = new SExprParser(tokens, diag);
        var sExprs = parser.ParseAll();
        if (options.MergeImports)
            sExprs = ImportMerger.MergeImports(sExprs);
        var layout = CommentAttacher.Attach(sExprs, comments, tokens);
        return PrettyPrinter.Format(sExprs, options, layout);
    }
}
