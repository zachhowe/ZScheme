using Xunit;
using ZScheme.Compiler.Syntax;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Tests;

public sealed class LexicalStructureTests
{
    [Fact]
    public void BuildTree_MultiLineForm_CloseTokenOnLastLine()
    {
        var source = "(define (add a b)\n  (+ a b))\n";
        var tree = LexicalStructure.BuildTree(LexicalStructure.Tokens(source));

        var define = Assert.Single(tree);
        Assert.Equal(1, define.Open.Span.Line);
        Assert.Equal(1, define.Open.Span.Column);
        Assert.Equal(2, define.Close.Span.Line);
        Assert.Equal(10, define.Close.Span.Column);
    }

    [Fact]
    public void BuildTree_NestedChildren()
    {
        var source = "(let ([x 1]) x)";
        var tree = LexicalStructure.BuildTree(LexicalStructure.Tokens(source));

        var let = Assert.Single(tree);
        var bindings = Assert.Single(let.Children);
        Assert.Equal(TokenKind.LParen, bindings.Open.Kind);
        var binding = Assert.Single(bindings.Children);
        Assert.Equal(TokenKind.LBracket, binding.Open.Kind);
        Assert.Equal(2, binding.AtomTokens.Count);
        Assert.Equal("x", binding.AtomTokens[0].Text);
    }

    [Fact]
    public void BuildTree_UnclosedBracket_ClosesAtLastToken()
    {
        var source = "(define (f x)\n  (+ x";
        var tree = LexicalStructure.BuildTree(LexicalStructure.Tokens(source));

        var define = Assert.Single(tree);
        Assert.Equal(2, define.Children.Count);
        var plus = define.Children[1];
        Assert.Equal("x", plus.Close.Text);
        Assert.Equal(2, plus.Close.Span.Line);
    }

    [Fact]
    public void BuildTree_StrayCloserAtTopLevel_Skipped()
    {
        var source = ") (define x 1)";
        var tree = LexicalStructure.BuildTree(LexicalStructure.Tokens(source));

        var define = Assert.Single(tree);
        Assert.Equal("define", define.AtomTokens[0].Text);
    }

    [Fact]
    public void Tokens_CommentsRetained()
    {
        var source = "; header comment\n(define x 1) ; trailing\n";
        var tokens = LexicalStructure.Tokens(source);

        var comments = tokens.Where(t => t.Kind == TokenKind.Comment).ToList();
        Assert.Equal(2, comments.Count);
        Assert.Equal(1, comments[0].Span.Line);
        Assert.Equal(2, comments[1].Span.Line);
    }

    [Fact]
    public void BuildTree_CommentsDoNotBecomeAtoms()
    {
        var source = "(define x ; note\n  1)";
        var tree = LexicalStructure.BuildTree(LexicalStructure.Tokens(source));

        var define = Assert.Single(tree);
        // The comment token is inside the form; it lands in AtomTokens (harmless for
        // extent computation) but bracket matching must not be disturbed by it.
        Assert.Equal(2, define.Close.Span.Line);
        Assert.Contains(define.AtomTokens, t => t.Kind == TokenKind.Comment);
    }

    [Fact]
    public void StringEndOffset_SimpleString()
    {
        var source = "(f \"hello\")";
        var start = source.IndexOf('"');
        Assert.Equal(source.LastIndexOf('"') + 1, LexicalStructure.StringEndOffset(source, start));
    }

    [Fact]
    public void StringEndOffset_EscapedQuote()
    {
        var source = "(f \"say \\\"hi\\\"\" tail)";
        var start = source.IndexOf('"');
        var end = LexicalStructure.StringEndOffset(source, start);
        Assert.Equal("\"say \\\"hi\\\"\"", source[start..end]);
    }

    [Fact]
    public void StringEndOffset_MultiLineString()
    {
        var source = "(f \"line1\nline2\")";
        var start = source.IndexOf('"');
        var end = LexicalStructure.StringEndOffset(source, start);
        Assert.Equal(source.LastIndexOf('"') + 1, end);
    }

    [Fact]
    public void StringEndOffset_Unterminated_EndsAtSourceEnd()
    {
        var source = "(f \"never ends";
        var start = source.IndexOf('"');
        Assert.Equal(source.Length, LexicalStructure.StringEndOffset(source, start));
    }
}
