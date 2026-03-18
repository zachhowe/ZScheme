namespace ZScript.Compiler.Syntax;

using ZScript.Compiler.Diagnostics;

public sealed class SExprParser
{
    private readonly List<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _pos;

    public SExprParser(List<Token> tokens, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public DiagnosticBag Diagnostics => _diagnostics;

    public List<SExpr> ParseAll()
    {
        var exprs = new List<SExpr>();
        while (Current.Kind != TokenKind.Eof)
        {
            exprs.Add(ParseExpr());
        }
        return exprs;
    }

    public SExpr ParseExpr()
    {
        var token = Current;

        switch (token.Kind)
        {
            case TokenKind.LParen:
                return ParseList();
            case TokenKind.LBracket:
                return ParseBracketList();
            case TokenKind.Symbol:
            case TokenKind.IntLit:
            case TokenKind.FloatLit:
            case TokenKind.StringLit:
            case TokenKind.BoolLit:
            case TokenKind.Colon:
            case TokenKind.Dot:
                Advance();
                return new SExpr.Atom(token);
            case TokenKind.RParen:
            case TokenKind.RBracket:
                _diagnostics.Error($"Unexpected '{token.Text}'", token.Span);
                Advance();
                return new SExpr.Atom(token);
            case TokenKind.Eof:
                _diagnostics.Error("Unexpected end of input", token.Span);
                return new SExpr.Atom(token);
            default:
                _diagnostics.Error($"Unexpected token: {token}", token.Span);
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
        {
            items.Add(ParseExpr());
        }

        if (Current.Kind == TokenKind.RParen)
        {
            Advance();
        }
        else
        {
            _diagnostics.Error("Expected ')'", Current.Span);
        }

        var span = new SourceSpan(open.Span.File, open.Span.Line, open.Span.Column,
            Current.Span.Column - open.Span.Column + 1);
        return new SExpr.SList(items, span);
    }

    private SExpr.BracketList ParseBracketList()
    {
        var open = Current;
        Advance(); // skip '['
        var items = new List<SExpr>();

        while (Current.Kind != TokenKind.RBracket && Current.Kind != TokenKind.Eof)
        {
            items.Add(ParseExpr());
        }

        if (Current.Kind == TokenKind.RBracket)
        {
            Advance();
        }
        else
        {
            _diagnostics.Error("Expected ']'", Current.Span);
        }

        var span = new SourceSpan(open.Span.File, open.Span.Line, open.Span.Column,
            Current.Span.Column - open.Span.Column + 1);
        return new SExpr.BracketList(items, span);
    }

    private Token Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];

    private void Advance()
    {
        if (_pos < _tokens.Count)
            _pos++;
    }
}
