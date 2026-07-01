using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Syntax;

public sealed class SExprParser(List<Token> tokens, DiagnosticBag diagnostics)
{
    private int _pos;

    private Token Current => _pos < tokens.Count ? tokens[_pos] : tokens[^1];

    public List<SExpr> ParseAll()
    {
        var exprs = new List<SExpr>();
        while (Current.Kind != TokenKind.Eof)
            exprs.Add(ParseExpr());
        return exprs;
    }

    private SExpr ParseExpr()
    {
        var token = Current;

        switch (token.Kind)
        {
            case TokenKind.LParen:
                return ParseList();
            case TokenKind.LBracket:
                return ParseBracketList();
            case TokenKind.Quote:
                return DesugarQuote("quote", token);
            case TokenKind.Quasiquote:
                return DesugarQuote("quasiquote", token);
            case TokenKind.Unquote:
                return DesugarQuote("unquote", token);
            case TokenKind.UnquoteSplicing:
                return DesugarQuote("unquote-splicing", token);
            case TokenKind.Symbol:
            case TokenKind.IntLit:
            case TokenKind.FloatLit:
            case TokenKind.StringLit:
            case TokenKind.BoolLit:
            case TokenKind.NullLit:
            case TokenKind.Colon:
            case TokenKind.Dot:
                Advance();
                return new SExpr.Atom(token);
            case TokenKind.RParen:
            case TokenKind.RBracket:
                diagnostics.Error($"Unexpected '{token.Text}'", token.Span);
                Advance();
                return new SExpr.Atom(token);
            case TokenKind.Eof:
                diagnostics.Error("Unexpected end of input", token.Span);
                return new SExpr.Atom(token);
            case TokenKind.Comment:
            default:
                diagnostics.Error($"Unexpected token: {token}", token.Span);
                Advance();
                return new SExpr.Atom(token);
        }
    }

    private SExpr.SList ParseList()
    {
        var open = Current;
        Advance(); // skip '('
        var items = new List<SExpr>();

        while (Current.Kind != TokenKind.RParen && Current.Kind != TokenKind.Eof)
            items.Add(ParseExpr());

        if (Current.Kind == TokenKind.RParen)
            Advance();
        else
            diagnostics.Error("Expected ')'", Current.Span);

        var span = new SourceSpan(
            open.Span.File,
            open.Span.Line,
            open.Span.Column,
            Current.Span.Column - open.Span.Column + 1
        );
        return new SExpr.SList(items, span);
    }

    private SExpr.BracketList ParseBracketList()
    {
        var open = Current;
        Advance(); // skip '['
        var items = new List<SExpr>();

        while (Current.Kind != TokenKind.RBracket && Current.Kind != TokenKind.Eof)
            items.Add(ParseExpr());

        if (Current.Kind == TokenKind.RBracket)
            Advance();
        else
            diagnostics.Error("Expected ']'", Current.Span);

        var span = new SourceSpan(
            open.Span.File,
            open.Span.Line,
            open.Span.Column,
            Current.Span.Column - open.Span.Column + 1
        );
        return new SExpr.BracketList(items, span);
    }

    private SExpr DesugarQuote(string formName, Token quoteToken)
    {
        Advance(); // skip the quote token
        var inner = ParseExpr();
        var nameToken = new Token(TokenKind.Symbol, formName, quoteToken.Span);
        var nameAtom = new SExpr.Atom(nameToken);
        return new SExpr.SList([nameAtom, inner], quoteToken.Span);
    }

    private void Advance()
    {
        if (_pos < tokens.Count)
            _pos++;
    }
}
