namespace ZScript.Compiler.Tests.Syntax;

using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Syntax;
using Xunit;

public class LexerTests
{
    private static List<Token> Lex(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return tokens;
    }

    private static List<Token> LexWithErrors(string source, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diagnostics);
        return lexer.Tokenize();
    }

    [Fact]
    public void EmptyInput_ReturnsEof()
    {
        var tokens = Lex("");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.Eof, tokens[0].Kind);
    }

    [Fact]
    public void Parens()
    {
        var tokens = Lex("()");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.LParen, tokens[0].Kind);
        Assert.Equal(TokenKind.RParen, tokens[1].Kind);
        Assert.Equal(TokenKind.Eof, tokens[2].Kind);
    }

    [Fact]
    public void Brackets()
    {
        var tokens = Lex("[]");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.LBracket, tokens[0].Kind);
        Assert.Equal(TokenKind.RBracket, tokens[1].Kind);
    }

    [Fact]
    public void IntLiteral()
    {
        var tokens = Lex("42");
        Assert.Equal(TokenKind.IntLit, tokens[0].Kind);
        Assert.Equal("42", tokens[0].Text);
    }

    [Fact]
    public void NegativeIntLiteral()
    {
        var tokens = Lex("-7");
        Assert.Equal(TokenKind.IntLit, tokens[0].Kind);
        Assert.Equal("-7", tokens[0].Text);
    }

    [Fact]
    public void FloatLiteral()
    {
        var tokens = Lex("3.14");
        Assert.Equal(TokenKind.FloatLit, tokens[0].Kind);
        Assert.Equal("3.14", tokens[0].Text);
    }

    [Fact]
    public void FloatWithSuffix()
    {
        var tokens = Lex("3f");
        Assert.Equal(TokenKind.FloatLit, tokens[0].Kind);
        Assert.Equal("3f", tokens[0].Text);
    }

    [Fact]
    public void StringLiteral()
    {
        var tokens = Lex("\"hello world\"");
        Assert.Equal(TokenKind.StringLit, tokens[0].Kind);
        Assert.Equal("hello world", tokens[0].Text);
    }

    [Fact]
    public void StringWithEscapes()
    {
        var tokens = Lex("\"hello\\nworld\"");
        Assert.Equal(TokenKind.StringLit, tokens[0].Kind);
        Assert.Equal("hello\nworld", tokens[0].Text);
    }

    [Fact]
    public void BoolLiterals()
    {
        var tokens = Lex("true false");
        Assert.Equal(TokenKind.BoolLit, tokens[0].Kind);
        Assert.Equal("true", tokens[0].Text);
        Assert.Equal(TokenKind.BoolLit, tokens[1].Kind);
        Assert.Equal("false", tokens[1].Text);
    }

    [Fact]
    public void Symbol()
    {
        var tokens = Lex("define");
        Assert.Equal(TokenKind.Symbol, tokens[0].Kind);
        Assert.Equal("define", tokens[0].Text);
    }

    [Fact]
    public void OperatorSymbols()
    {
        var tokens = Lex("+ - * / = < > <= >= !=");
        Assert.All(tokens.Take(tokens.Count - 1), t => Assert.Equal(TokenKind.Symbol, t.Kind));
        Assert.Equal("+", tokens[0].Text);
        Assert.Equal("-", tokens[1].Text);
        Assert.Equal("*", tokens[2].Text);
        Assert.Equal("/", tokens[3].Text);
        Assert.Equal("=", tokens[4].Text);
        Assert.Equal("<", tokens[5].Text);
        Assert.Equal(">", tokens[6].Text);
        Assert.Equal("<=", tokens[7].Text);
        Assert.Equal(">=", tokens[8].Text);
        Assert.Equal("!=", tokens[9].Text);
    }

    [Fact]
    public void Colon()
    {
        var tokens = Lex(":");
        Assert.Equal(TokenKind.Colon, tokens[0].Kind);
    }

    [Fact]
    public void Dot()
    {
        var tokens = Lex(".");
        Assert.Equal(TokenKind.Dot, tokens[0].Kind);
    }

    [Fact]
    public void CommentsAreSkipped()
    {
        var tokens = Lex(";; this is a comment\n42");
        Assert.Equal(TokenKind.IntLit, tokens[0].Kind);
        Assert.Equal("42", tokens[0].Text);
    }

    [Fact]
    public void ComplexExpression()
    {
        var tokens = Lex("(define (add [x : Int] [y : Int]) : Int (+ x y))");

        var kinds = tokens.Select(t => t.Kind).ToList();
        Assert.Equal(TokenKind.LParen, kinds[0]);   // (
        Assert.Equal(TokenKind.Symbol, kinds[1]);    // define
        Assert.Equal(TokenKind.LParen, kinds[2]);    // (
        Assert.Equal(TokenKind.Symbol, kinds[3]);    // add
        Assert.Equal(TokenKind.LBracket, kinds[4]);  // [
        Assert.Equal(TokenKind.Symbol, kinds[5]);    // x
        Assert.Equal(TokenKind.Colon, kinds[6]);     // :
        Assert.Equal(TokenKind.Symbol, kinds[7]);    // Int
        Assert.Equal(TokenKind.RBracket, kinds[8]);  // ]
    }

    [Fact]
    public void LineTracking()
    {
        var tokens = Lex("(\n  42\n)");
        Assert.Equal(1, tokens[0].Span.Line); // (
        Assert.Equal(2, tokens[1].Span.Line); // 42
        Assert.Equal(3, tokens[2].Span.Line); // )
    }

    [Fact]
    public void PipeOperator()
    {
        var tokens = Lex("|>");
        Assert.Equal(TokenKind.Symbol, tokens[0].Kind);
        Assert.Equal("|>", tokens[0].Text);
    }

    [Fact]
    public void QuestionMark()
    {
        var tokens = Lex("?");
        Assert.Equal(TokenKind.Symbol, tokens[0].Kind);
        Assert.Equal("?", tokens[0].Text);
    }

    [Fact]
    public void HyphenatedSymbol()
    {
        var tokens = Lex("import-clr");
        Assert.Equal(TokenKind.Symbol, tokens[0].Kind);
        Assert.Equal("import-clr", tokens[0].Text);
    }

    [Fact]
    public void SlashInSymbol()
    {
        var tokens = Lex("System.Math/Sqrt");
        Assert.Equal(TokenKind.Symbol, tokens[0].Kind);
        Assert.Equal("System.Math/Sqrt", tokens[0].Text);
    }

    [Fact]
    public void UnterminatedString_ReportsError()
    {
        var tokens = LexWithErrors("\"hello", out var diag);
        Assert.True(diag.HasErrors);
    }
}
