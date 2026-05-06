using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Syntax;

public class SExprParserTests
{
    private static List<SExpr> Parse(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var result = parser.ParseAll();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return result;
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var exprs = Parse("");
        Assert.Empty(exprs);
    }

    [Fact]
    public void SingleAtom()
    {
        var exprs = Parse("42");
        Assert.Single(exprs);
        var atom = Assert.IsType<SExpr.Atom>(exprs[0]);
        Assert.Equal("42", atom.Text);
        Assert.Equal(TokenKind.IntLit, atom.Kind);
    }

    [Fact]
    public void SimpleList()
    {
        var exprs = Parse("(+ 1 2)");
        Assert.Single(exprs);
        var list = Assert.IsType<SExpr.SList>(exprs[0]);
        Assert.Equal(3, list.Items.Count);
        Assert.Equal("+", ((SExpr.Atom)list.Items[0]).Text);
        Assert.Equal("1", ((SExpr.Atom)list.Items[1]).Text);
        Assert.Equal("2", ((SExpr.Atom)list.Items[2]).Text);
    }

    [Fact]
    public void NestedList()
    {
        var exprs = Parse("(+ (* 2 3) 4)");
        Assert.Single(exprs);
        var outer = Assert.IsType<SExpr.SList>(exprs[0]);
        Assert.Equal(3, outer.Items.Count);
        var inner = Assert.IsType<SExpr.SList>(outer.Items[1]);
        Assert.Equal(3, inner.Items.Count);
        Assert.Equal("*", ((SExpr.Atom)inner.Items[0]).Text);
    }

    [Fact]
    public void BracketList()
    {
        var exprs = Parse("[x : Int]");
        Assert.Single(exprs);
        var bracket = Assert.IsType<SExpr.BracketList>(exprs[0]);
        Assert.Equal(3, bracket.Items.Count);
        Assert.Equal("x", ((SExpr.Atom)bracket.Items[0]).Text);
        Assert.Equal(":", ((SExpr.Atom)bracket.Items[1]).Text);
        Assert.Equal("Int", ((SExpr.Atom)bracket.Items[2]).Text);
    }

    [Fact]
    public void DefineFunction()
    {
        var exprs = Parse("(define (add [x : Int] [y : Int]) : Int (+ x y))");
        Assert.Single(exprs);
        var outer = Assert.IsType<SExpr.SList>(exprs[0]);

        // (define ...)
        Assert.Equal("define", ((SExpr.Atom)outer.Items[0]).Text);

        // (add [x : Int] [y : Int])
        var sig = Assert.IsType<SExpr.SList>(outer.Items[1]);
        Assert.Equal("add", ((SExpr.Atom)sig.Items[0]).Text);

        var param1 = Assert.IsType<SExpr.BracketList>(sig.Items[1]);
        Assert.Equal("x", ((SExpr.Atom)param1.Items[0]).Text);

        var param2 = Assert.IsType<SExpr.BracketList>(sig.Items[2]);
        Assert.Equal("y", ((SExpr.Atom)param2.Items[0]).Text);

        // : Int
        Assert.Equal(":", ((SExpr.Atom)outer.Items[2]).Text);
        Assert.Equal("Int", ((SExpr.Atom)outer.Items[3]).Text);

        // (+ x y)
        var body = Assert.IsType<SExpr.SList>(outer.Items[4]);
        Assert.Equal("+", ((SExpr.Atom)body.Items[0]).Text);
    }

    [Fact]
    public void MultipleTopLevelForms()
    {
        var exprs = Parse("(define x 5) (+ x 1)");
        Assert.Equal(2, exprs.Count);
        Assert.IsType<SExpr.SList>(exprs[0]);
        Assert.IsType<SExpr.SList>(exprs[1]);
    }

    [Fact]
    public void StringLiteralAtom()
    {
        var exprs = Parse("\"hello\"");
        Assert.Single(exprs);
        var atom = Assert.IsType<SExpr.Atom>(exprs[0]);
        Assert.Equal(TokenKind.StringLit, atom.Kind);
        Assert.Equal("hello", atom.Text);
    }

    [Fact]
    public void BoolLiteralAtom()
    {
        var exprs = Parse("#t");
        Assert.Single(exprs);
        var atom = Assert.IsType<SExpr.Atom>(exprs[0]);
        Assert.Equal(TokenKind.BoolLit, atom.Kind);
    }

    [Fact]
    public void RecordDefinition()
    {
        var exprs = Parse("(define-record Point [x : Float] [y : Float])");
        Assert.Single(exprs);
        var list = Assert.IsType<SExpr.SList>(exprs[0]);
        Assert.Equal("define-record", ((SExpr.Atom)list.Items[0]).Text);
        Assert.Equal("Point", ((SExpr.Atom)list.Items[1]).Text);
        Assert.IsType<SExpr.BracketList>(list.Items[2]);
        Assert.IsType<SExpr.BracketList>(list.Items[3]);
    }

    [Fact]
    public void UnionDefinition()
    {
        var exprs = Parse("(define-union Shape (Circle [radius : Float]) (Rect [w : Float] [h : Float]))");
        Assert.Single(exprs);
        var list = Assert.IsType<SExpr.SList>(exprs[0]);
        Assert.Equal("define-union", ((SExpr.Atom)list.Items[0]).Text);
        Assert.Equal("Shape", ((SExpr.Atom)list.Items[1]).Text);
    }

    [Fact]
    public void MatchExpression()
    {
        var source = @"(match shape
  [(Circle r) (* 3.14 (* r r))]
  [(Rect w h) (* w h)])";
        var exprs = Parse(source);
        Assert.Single(exprs);
        var list = Assert.IsType<SExpr.SList>(exprs[0]);
        Assert.Equal("match", ((SExpr.Atom)list.Items[0]).Text);
        Assert.Equal("shape", ((SExpr.Atom)list.Items[1]).Text);
        // Two match arms as bracket lists
        Assert.IsType<SExpr.BracketList>(list.Items[2]);
        Assert.IsType<SExpr.BracketList>(list.Items[3]);
    }

    [Fact]
    public void PipeExpression()
    {
        var exprs = Parse("(|> x (f a) (g b))");
        Assert.Single(exprs);
        var list = Assert.IsType<SExpr.SList>(exprs[0]);
        Assert.Equal("|>", ((SExpr.Atom)list.Items[0]).Text);
    }

    [Fact]
    public void LetExpression()
    {
        var exprs = Parse("(let [x 5] (+ x 1))");
        Assert.Single(exprs);
        var list = Assert.IsType<SExpr.SList>(exprs[0]);
        Assert.Equal("let", ((SExpr.Atom)list.Items[0]).Text);
        var binding = Assert.IsType<SExpr.BracketList>(list.Items[1]);
        Assert.Equal("x", ((SExpr.Atom)binding.Items[0]).Text);
        Assert.Equal("5", ((SExpr.Atom)binding.Items[1]).Text);
    }

    [Fact]
    public void LambdaExpression()
    {
        var exprs = Parse("(lambda (x y) (+ x y))");
        Assert.Single(exprs);
        var list = Assert.IsType<SExpr.SList>(exprs[0]);
        Assert.Equal("lambda", ((SExpr.Atom)list.Items[0]).Text);
    }

    [Fact]
    public void ImportClr()
    {
        var exprs = Parse("(import-clr [sqrt System.Math/Sqrt] [console System.Console/WriteLine])");
        Assert.Single(exprs);
        var list = Assert.IsType<SExpr.SList>(exprs[0]);
        Assert.Equal("import-clr", ((SExpr.Atom)list.Items[0]).Text);
        Assert.Equal(3, list.Items.Count);
    }

    [Fact]
    public void ToStringRoundTrip()
    {
        var exprs = Parse("(+ 1 2)");
        var str = exprs[0].ToString();
        Assert.Equal("(+ 1 2)", str);
    }
}
