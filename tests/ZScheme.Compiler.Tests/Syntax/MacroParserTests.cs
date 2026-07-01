using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Syntax;

public class MacroParserTests
{
    private static MacroDefinition? ParseMacro(string source)
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
        return result;
    }

    [Fact]
    public void ParsesSimpleMacro()
    {
        var def = ParseMacro("(define-syntax my-if (syntax-rules () [(my-if c t e) (if c t e)]))");
        Assert.NotNull(def);
        Assert.Equal("my-if", def.Name);
        Assert.Empty(def.Literals);
        Assert.Single(def.Rules);
    }

    [Fact]
    public void ParsesLiterals()
    {
        var def = ParseMacro(
            "(define-syntax my-let (syntax-rules (in) [(my-let x in body) body]))"
        );
        Assert.NotNull(def);
        Assert.Single(def.Literals);
        Assert.Equal("in", def.Literals[0]);
    }

    [Fact]
    public void ParsesEllipsisPattern()
    {
        var def = ParseMacro(
            "(define-syntax when (syntax-rules () [(when cond body ...) (if cond (begin body ...) unit)]))"
        );
        Assert.NotNull(def);
        var rule = def.Rules[0];
        Assert.IsType<MacroPattern.PatList>(rule.Pattern);
        var patList = (MacroPattern.PatList)rule.Pattern;
        // (when cond body ...)
        Assert.Equal(3, patList.Elements.Count);
        Assert.IsType<MacroPattern.Literal>(patList.Elements[0]); // when
        Assert.IsType<MacroPattern.Variable>(patList.Elements[1]); // cond
        Assert.IsType<MacroPattern.Ellipsis>(patList.Elements[2]); // body ...
    }

    [Fact]
    public void ParsesMultipleRules()
    {
        var def = ParseMacro(
            @"
            (define-syntax my-and
              (syntax-rules ()
                [(my-and) #t]
                [(my-and x) x]
                [(my-and x rest ...) (if x (my-and rest ...) #f)]))"
        );
        Assert.NotNull(def);
        Assert.Equal(3, def.Rules.Count);
    }

    [Fact]
    public void ParsesTemplateEllipsis()
    {
        var def = ParseMacro(
            "(define-syntax when (syntax-rules () [(when cond body ...) (begin body ...)]))"
        );
        Assert.NotNull(def);
        var template = (MacroTemplate.TList)def.Rules[0].Template;
        // (begin body ...)
        Assert.Equal(2, template.Elements.Count);
        Assert.IsType<MacroTemplate.Datum>(template.Elements[0]); // begin
        Assert.IsType<MacroTemplate.Ellipsis>(template.Elements[1]); // body ...
    }

    [Fact]
    public void RejectsInvalidForm()
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer("(define-syntax)", "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();

        var macroParser = new MacroParser(diag);
        var result = macroParser.Parse((SExpr.SList)sexprs[0]);
        Assert.True(diag.HasErrors);
    }
}
