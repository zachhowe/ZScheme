using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Syntax;

public class MacroRulePrinterTests
{
    private static MacroDefinition ParseMacro(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));

        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.Single(sexprs);

        var macroParser = new MacroParser(diag);
        var result = macroParser.Parse((SExpr.SList)sexprs[0]);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        Assert.NotNull(result);
        return result;
    }

    private static MacroRule ParseSingleRule(string source)
    {
        var def = ParseMacro(source);
        return Assert.Single(def.Rules);
    }

    [Fact]
    public void PrintsSimpleRuleAsPatternArrowTemplate()
    {
        var rule = ParseSingleRule(
            "(define-syntax my-if (syntax-rules () [(my-if c t e) (if c t e)]))"
        );
        Assert.Equal("(my-if c t e) => (if c t e)", MacroRulePrinter.Print(rule));
    }

    [Fact]
    public void PrintsEllipsisPatternAndTemplate()
    {
        var rule = ParseSingleRule(
            "(define-syntax when (syntax-rules () [(when cond body ...) (if cond (begin body ...) unit)]))"
        );
        Assert.Equal("(when cond body ...)", MacroRulePrinter.Print(rule.Pattern));
        Assert.Equal("(if cond (begin body ...) unit)", MacroRulePrinter.Print(rule.Template));
    }

    [Fact]
    public void PrintsBracketListsWithSquareBrackets()
    {
        var rule = ParseSingleRule(
            "(define-syntax my-let (syntax-rules () [(my-let [x v] body) ((lambda [x] body) v)]))"
        );
        Assert.Equal("(my-let [x v] body)", MacroRulePrinter.Print(rule.Pattern));
        Assert.Equal("((lambda [x] body) v)", MacroRulePrinter.Print(rule.Template));
    }

    [Fact]
    public void PrintsWildcardAsUnderscore()
    {
        var pattern = new MacroPattern.PatList(
            [
                new MacroPattern.Literal("m", SourceSpan.None),
                new MacroPattern.Wildcard(SourceSpan.None),
            ],
            SourceSpan.None
        );
        Assert.Equal("(m _)", MacroRulePrinter.Print(pattern));
    }

    [Fact]
    public void PrintsLiteralPatternNamesBare()
    {
        var def = ParseMacro(
            "(define-syntax my-let (syntax-rules (in) [(my-let x in body) body]))"
        );
        var rule = Assert.Single(def.Rules);
        Assert.Equal("(my-let x in body)", MacroRulePrinter.Print(rule.Pattern));
    }

    [Fact]
    public void PrintsDatumTemplatesViaSExprPrinter()
    {
        var rule = ParseSingleRule(
            "(define-syntax answer (syntax-rules () [(answer) (id 42 \"s\")]))"
        );
        Assert.Equal("(id 42 \"s\")", MacroRulePrinter.Print(rule.Template));
    }
}
