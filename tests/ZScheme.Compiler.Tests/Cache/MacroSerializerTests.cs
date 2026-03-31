using Xunit;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Cache;

public sealed class MacroSerializerTests
{
    private static readonly SourceSpan S = SourceSpan.None;

    private static SExpr.Atom Sym(string text)
    {
        return new SExpr.Atom(new Token(TokenKind.Symbol, text, S));
    }

    [Fact]
    public void RoundTrip_SimpleMacro_LiteralAndVariablePattern()
    {
        var macro = new MacroDefinition(
            "my-macro",
            ["let"],
            [
                new MacroRule(
                    new MacroPattern.PatList([
                        new MacroPattern.Literal("my-macro", S),
                        new MacroPattern.Variable("x", S)
                    ], S),
                    new MacroTemplate.TList([
                        new MacroTemplate.Datum(Sym("begin"), S),
                        new MacroTemplate.Variable("x", S)
                    ], S),
                    S)
            ],
            S);

        var json = MacroSerializer.Serialize(macro);
        var result = MacroSerializer.Deserialize(json);

        Assert.Equal("my-macro", result.Name);
        Assert.Equal(["let"], result.Literals);
        Assert.Single(result.Rules);

        var rule = result.Rules[0];
        var pat = Assert.IsType<MacroPattern.PatList>(rule.Pattern);
        Assert.Equal(2, pat.Elements.Count);
        Assert.IsType<MacroPattern.Literal>(pat.Elements[0]);
        Assert.Equal("my-macro", ((MacroPattern.Literal)pat.Elements[0]).Name);
        Assert.IsType<MacroPattern.Variable>(pat.Elements[1]);
        Assert.Equal("x", ((MacroPattern.Variable)pat.Elements[1]).Name);

        var tmpl = Assert.IsType<MacroTemplate.TList>(rule.Template);
        Assert.Equal(2, tmpl.Elements.Count);
        var datum = Assert.IsType<MacroTemplate.Datum>(tmpl.Elements[0]);
        var atom = Assert.IsType<SExpr.Atom>(datum.Value);
        Assert.Equal("begin", atom.Text);
        Assert.Equal(TokenKind.Symbol, atom.Kind);
        Assert.IsType<MacroTemplate.Variable>(tmpl.Elements[1]);
    }

    [Fact]
    public void RoundTrip_EllipsisPatternAndTemplate()
    {
        var macro = new MacroDefinition(
            "do-all",
            [],
            [
                new MacroRule(
                    new MacroPattern.PatList([
                        new MacroPattern.Literal("do-all", S),
                        new MacroPattern.Ellipsis(new MacroPattern.Variable("body", S), S)
                    ], S),
                    new MacroTemplate.TList([
                        new MacroTemplate.Datum(Sym("begin"), S),
                        new MacroTemplate.Ellipsis(new MacroTemplate.Variable("body", S), S)
                    ], S),
                    S)
            ],
            S);

        var json = MacroSerializer.Serialize(macro);
        var result = MacroSerializer.Deserialize(json);

        var pat = Assert.IsType<MacroPattern.PatList>(result.Rules[0].Pattern);
        var ellipsisPat = Assert.IsType<MacroPattern.Ellipsis>(pat.Elements[1]);
        Assert.Equal("body", ((MacroPattern.Variable)ellipsisPat.Inner).Name);

        var tmpl = Assert.IsType<MacroTemplate.TList>(result.Rules[0].Template);
        var ellipsisTmpl = Assert.IsType<MacroTemplate.Ellipsis>(tmpl.Elements[1]);
        Assert.Equal("body", ((MacroTemplate.Variable)ellipsisTmpl.Inner).Name);
    }

    [Fact]
    public void RoundTrip_WildcardPattern()
    {
        var macro = new MacroDefinition(
            "ignore",
            [],
            [
                new MacroRule(
                    new MacroPattern.PatList([
                        new MacroPattern.Literal("ignore", S),
                        new MacroPattern.Wildcard(S)
                    ], S),
                    new MacroTemplate.Datum(Sym("unit"), S),
                    S)
            ],
            S);

        var json = MacroSerializer.Serialize(macro);
        var result = MacroSerializer.Deserialize(json);

        var pat = Assert.IsType<MacroPattern.PatList>(result.Rules[0].Pattern);
        Assert.IsType<MacroPattern.Wildcard>(pat.Elements[1]);
    }

    [Fact]
    public void RoundTrip_TBracketListTemplate()
    {
        var macro = new MacroDefinition(
            "vec-wrap",
            [],
            [
                new MacroRule(
                    new MacroPattern.PatList([
                        new MacroPattern.Literal("vec-wrap", S),
                        new MacroPattern.Variable("x", S)
                    ], S),
                    new MacroTemplate.TBracketList([
                        new MacroTemplate.Variable("x", S)
                    ], S),
                    S)
            ],
            S);

        var json = MacroSerializer.Serialize(macro);
        var result = MacroSerializer.Deserialize(json);

        var tmpl = Assert.IsType<MacroTemplate.TBracketList>(result.Rules[0].Template);
        Assert.Single(tmpl.Elements);
        Assert.Equal("x", ((MacroTemplate.Variable)tmpl.Elements[0]).Name);
    }

    [Fact]
    public void RoundTrip_NestedPatList()
    {
        var macro = new MacroDefinition(
            "nested",
            [],
            [
                new MacroRule(
                    new MacroPattern.PatList([
                        new MacroPattern.Literal("nested", S),
                        new MacroPattern.PatList([
                            new MacroPattern.Variable("a", S),
                            new MacroPattern.Variable("b", S)
                        ], S)
                    ], S),
                    new MacroTemplate.Variable("a", S),
                    S)
            ],
            S);

        var json = MacroSerializer.Serialize(macro);
        var result = MacroSerializer.Deserialize(json);

        var outerPat = Assert.IsType<MacroPattern.PatList>(result.Rules[0].Pattern);
        var innerPat = Assert.IsType<MacroPattern.PatList>(outerPat.Elements[1]);
        Assert.Equal(2, innerPat.Elements.Count);
        Assert.Equal("a", ((MacroPattern.Variable)innerPat.Elements[0]).Name);
        Assert.Equal("b", ((MacroPattern.Variable)innerPat.Elements[1]).Name);
    }

    [Fact]
    public void RoundTrip_SExpr_AllVariants()
    {
        // Atom with various TokenKinds
        var atomSymbol = Sym("hello");
        var atomInt = new SExpr.Atom(new Token(TokenKind.IntLit, "42", S));
        var atomString = new SExpr.Atom(new Token(TokenKind.StringLit, "\"foo\"", S));
        var atomBool = new SExpr.Atom(new Token(TokenKind.BoolLit, "#t", S));
        var atomFloat = new SExpr.Atom(new Token(TokenKind.FloatLit, "3.14", S));

        // SList
        var sList = new SExpr.SList([atomSymbol, atomInt], S);

        // BracketList
        var bracketList = new SExpr.BracketList([atomString, atomBool], S);

        // Wrap each in a datum template for round-trip testing
        var macro = new MacroDefinition(
            "sexpr-test",
            [],
            [
                new MacroRule(
                    new MacroPattern.PatList([new MacroPattern.Literal("sexpr-test", S)], S),
                    new MacroTemplate.TList([
                        new MacroTemplate.Datum(atomSymbol, S),
                        new MacroTemplate.Datum(atomInt, S),
                        new MacroTemplate.Datum(atomString, S),
                        new MacroTemplate.Datum(atomBool, S),
                        new MacroTemplate.Datum(atomFloat, S),
                        new MacroTemplate.Datum(sList, S),
                        new MacroTemplate.Datum(bracketList, S)
                    ], S),
                    S)
            ],
            S);

        var json = MacroSerializer.Serialize(macro);
        var result = MacroSerializer.Deserialize(json);

        var tmpl = Assert.IsType<MacroTemplate.TList>(result.Rules[0].Template);
        Assert.Equal(7, tmpl.Elements.Count);

        // Atom: Symbol
        var d0 = Assert.IsType<MacroTemplate.Datum>(tmpl.Elements[0]);
        var a0 = Assert.IsType<SExpr.Atom>(d0.Value);
        Assert.Equal(TokenKind.Symbol, a0.Kind);
        Assert.Equal("hello", a0.Text);

        // Atom: IntLit
        var d1 = Assert.IsType<MacroTemplate.Datum>(tmpl.Elements[1]);
        var a1 = Assert.IsType<SExpr.Atom>(d1.Value);
        Assert.Equal(TokenKind.IntLit, a1.Kind);
        Assert.Equal("42", a1.Text);

        // Atom: StringLit
        var d2 = Assert.IsType<MacroTemplate.Datum>(tmpl.Elements[2]);
        var a2 = Assert.IsType<SExpr.Atom>(d2.Value);
        Assert.Equal(TokenKind.StringLit, a2.Kind);
        Assert.Equal("\"foo\"", a2.Text);

        // Atom: BoolLit
        var d3 = Assert.IsType<MacroTemplate.Datum>(tmpl.Elements[3]);
        var a3 = Assert.IsType<SExpr.Atom>(d3.Value);
        Assert.Equal(TokenKind.BoolLit, a3.Kind);
        Assert.Equal("#t", a3.Text);

        // Atom: FloatLit
        var d4 = Assert.IsType<MacroTemplate.Datum>(tmpl.Elements[4]);
        var a4 = Assert.IsType<SExpr.Atom>(d4.Value);
        Assert.Equal(TokenKind.FloatLit, a4.Kind);
        Assert.Equal("3.14", a4.Text);

        // SList
        var d5 = Assert.IsType<MacroTemplate.Datum>(tmpl.Elements[5]);
        var sl = Assert.IsType<SExpr.SList>(d5.Value);
        Assert.Equal(2, sl.Items.Count);
        Assert.Equal("hello", ((SExpr.Atom)sl.Items[0]).Text);
        Assert.Equal("42", ((SExpr.Atom)sl.Items[1]).Text);

        // BracketList
        var d6 = Assert.IsType<MacroTemplate.Datum>(tmpl.Elements[6]);
        var bl = Assert.IsType<SExpr.BracketList>(d6.Value);
        Assert.Equal(2, bl.Items.Count);
        Assert.Equal("\"foo\"", ((SExpr.Atom)bl.Items[0]).Text);
        Assert.Equal("#t", ((SExpr.Atom)bl.Items[1]).Text);
    }

    [Fact]
    public void RoundTrip_MultipleRules()
    {
        var macro = new MacroDefinition(
            "multi",
            [],
            [
                new MacroRule(
                    new MacroPattern.PatList([
                        new MacroPattern.Literal("multi", S),
                        new MacroPattern.Variable("x", S)
                    ], S),
                    new MacroTemplate.Variable("x", S),
                    S),
                new MacroRule(
                    new MacroPattern.PatList([
                        new MacroPattern.Literal("multi", S),
                        new MacroPattern.Variable("x", S),
                        new MacroPattern.Variable("y", S)
                    ], S),
                    new MacroTemplate.TList([
                        new MacroTemplate.Variable("x", S),
                        new MacroTemplate.Variable("y", S)
                    ], S),
                    S)
            ],
            S);

        var json = MacroSerializer.Serialize(macro);
        var result = MacroSerializer.Deserialize(json);

        Assert.Equal(2, result.Rules.Count);
        Assert.Equal("multi", result.Name);

        var pat1 = Assert.IsType<MacroPattern.PatList>(result.Rules[0].Pattern);
        Assert.Equal(2, pat1.Elements.Count);

        var pat2 = Assert.IsType<MacroPattern.PatList>(result.Rules[1].Pattern);
        Assert.Equal(3, pat2.Elements.Count);
    }

    [Fact]
    public void RoundTrip_EmptyLiteralsAndRules()
    {
        var macro = new MacroDefinition("empty", [], [], S);

        var json = MacroSerializer.Serialize(macro);
        var result = MacroSerializer.Deserialize(json);

        Assert.Equal("empty", result.Name);
        Assert.Empty(result.Literals);
        Assert.Empty(result.Rules);
    }
}
