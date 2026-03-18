namespace ZScript.Compiler.Syntax;

using ZScript.Compiler.Diagnostics;

public sealed class Lexer
{
    private readonly string _source;
    private readonly string _file;
    private readonly DiagnosticBag _diagnostics;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public Lexer(string source, string file, DiagnosticBag diagnostics)
    {
        _source = source;
        _file = file;
        _diagnostics = diagnostics;
    }

    public DiagnosticBag Diagnostics => _diagnostics;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = NextToken();
            if (token.Kind == TokenKind.Comment)
                continue;
            tokens.Add(token);
            if (token.Kind == TokenKind.Eof)
                break;
        }
        return tokens;
    }

    private Token NextToken()
    {
        SkipWhitespace();

        if (_pos >= _source.Length)
            return MakeToken(TokenKind.Eof, "", _line, _col);

        var ch = _source[_pos];
        var startLine = _line;
        var startCol = _col;

        switch (ch)
        {
            case '(':
                Advance();
                return MakeToken(TokenKind.LParen, "(", startLine, startCol);
            case ')':
                Advance();
                return MakeToken(TokenKind.RParen, ")", startLine, startCol);
            case '[':
                Advance();
                return MakeToken(TokenKind.LBracket, "[", startLine, startCol);
            case ']':
                Advance();
                return MakeToken(TokenKind.RBracket, "]", startLine, startCol);
            case ':':
                Advance();
                return MakeToken(TokenKind.Colon, ":", startLine, startCol);
            case '.':
                if (_pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
                    return ReadNumber();
                Advance();
                return MakeToken(TokenKind.Dot, ".", startLine, startCol);
            case ';':
                return ReadComment();
            case '"':
                return ReadString();
            default:
                if (ch == '-' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
                    return ReadNumber();
                if (char.IsDigit(ch))
                    return ReadNumber();
                if (IsSymbolStart(ch))
                    return ReadSymbol();
                Advance();
                _diagnostics.Error($"Unexpected character: '{ch}'",
                    new SourceSpan(_file, startLine, startCol, 1));
                return MakeToken(TokenKind.Symbol, ch.ToString(), startLine, startCol);
        }
    }

    private Token ReadNumber()
    {
        var startLine = _line;
        var startCol = _col;
        var start = _pos;
        var isFloat = false;

        if (Current == '-')
            Advance();

        while (_pos < _source.Length && char.IsDigit(Current))
            Advance();

        if (_pos < _source.Length && Current == '.' &&
            _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
        {
            isFloat = true;
            Advance(); // skip '.'
            while (_pos < _source.Length && char.IsDigit(Current))
                Advance();
        }

        // Check for float suffix 'f' or double suffix 'd'
        if (_pos < _source.Length && (Current == 'f' || Current == 'F'))
        {
            isFloat = true;
            Advance();
        }

        var text = _source[start.._pos];
        return MakeToken(isFloat ? TokenKind.FloatLit : TokenKind.IntLit, text, startLine, startCol);
    }

    private Token ReadString()
    {
        var startLine = _line;
        var startCol = _col;
        Advance(); // skip opening quote
        var start = _pos;
        var sb = new System.Text.StringBuilder();

        while (_pos < _source.Length && Current != '"')
        {
            if (Current == '\\' && _pos + 1 < _source.Length)
            {
                Advance();
                sb.Append(Current switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '"' => '"',
                    _ => Current
                });
                Advance();
            }
            else
            {
                sb.Append(Current);
                Advance();
            }
        }

        if (_pos >= _source.Length)
        {
            _diagnostics.Error("Unterminated string literal",
                new SourceSpan(_file, startLine, startCol, _pos - start + 1));
        }
        else
        {
            Advance(); // skip closing quote
        }

        return MakeToken(TokenKind.StringLit, sb.ToString(), startLine, startCol);
    }

    private Token ReadComment()
    {
        var startLine = _line;
        var startCol = _col;
        var start = _pos;

        while (_pos < _source.Length && Current != '\n')
            Advance();

        var text = _source[start.._pos];
        return MakeToken(TokenKind.Comment, text, startLine, startCol);
    }

    private Token ReadSymbol()
    {
        var startLine = _line;
        var startCol = _col;
        var start = _pos;

        while (_pos < _source.Length && IsSymbolContinue(Current))
            Advance();

        var text = _source[start.._pos];

        if (text is "true" or "false")
            return MakeToken(TokenKind.BoolLit, text, startLine, startCol);

        return MakeToken(TokenKind.Symbol, text, startLine, startCol);
    }

    private void SkipWhitespace()
    {
        while (_pos < _source.Length && char.IsWhiteSpace(Current))
            Advance();
    }

    private char Current => _source[_pos];

    private void Advance()
    {
        if (_pos < _source.Length)
        {
            if (_source[_pos] == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }
            _pos++;
        }
    }

    private Token MakeToken(TokenKind kind, string text, int line, int col) =>
        new(kind, text, new SourceSpan(_file, line, col, text.Length));

    private static bool IsSymbolStart(char c) =>
        char.IsLetter(c) || c is '_' or '+' or '-' or '*' or '/' or '=' or '<' or '>'
            or '!' or '?' or '&' or '|' or '%' or '^' or '~' or '#';

    private static bool IsSymbolContinue(char c) =>
        IsSymbolStart(c) || char.IsDigit(c) || c is '.' or '-' or '>';
}
