using System.Text;

namespace ZScheme.Fuzzer.Generation;

// Emits String-typed expressions and reduces them to Int for the compute body.
// Covers StringConst emission (both backends), lexer escape handling (\n, \t, \r,
// \\, \"), and string-append (which lowers to `System.String.Concat` in IL and
// `+` in C#). The Int reducer uses string equality `(= s1 s2)` → `(if ... 1 0)`,
// which is safe regardless of literal content so any escape-sequence divergence
// between the two backends surfaces via diffexec.
public sealed class StringExprGenerator
{
    private readonly GeneratorContext _ctx;
    private readonly ExprGenerator _exprs;

    public StringExprGenerator(GeneratorContext ctx, ExprGenerator exprs)
    {
        _ctx = ctx;
        _exprs = exprs;
    }

    // Produces a String-typed expression. Leaves: short literals exercising
    // escape sequences, or a String var from scope. Inner: `string-append`.
    public string GenString(Scope scope, int depth)
    {
        if (depth <= 0)
            return GenStringLeaf(scope);

        var weights = new List<(int Weight, Func<string> Gen)>
        {
            (4, () => GenStringLeaf(scope)),
            (3, () => GenStringAppend(scope, depth)),
        };
        return _ctx.PickWeighted(weights)();
    }

    // Reducer: `(if (= s1 s2) 1 0)`. Sometimes builds both strings the same way
    // so the equality is true, sometimes mixes for false. Either way the result
    // is a well-typed Int.
    public string StringEqualityToInt(Scope scope, int depth)
    {
        var a = GenString(scope, depth - 1);
        var b = GenString(scope, depth - 1);
        return $"(if (= {a} {b}) 1 0)";
    }

    private string GenStringLeaf(Scope scope)
    {
        var strVars = scope.GetVars(ExprType.String);
        if (strVars.Count > 0 && _ctx.Rng.NextDouble() < 0.5)
            return strVars[_ctx.Rng.Next(strVars.Count)];

        return $"\"{EscapedLiteralBody()}\"";
    }

    private string GenStringAppend(Scope scope, int depth)
    {
        var a = GenString(scope, depth - 1);
        var b = GenString(scope, depth - 1);
        return $"(string-append {a} {b})";
    }

    // Builds a short literal body that includes some escape-sequence coverage.
    // Output is already-escaped source text ready to be wrapped in `"..."`.
    private string EscapedLiteralBody()
    {
        // When enabled, occasionally emit raw non-ASCII content. The lexer has no
        // `\u` escape (it only recognises \n \t \r \\ \"), so unicode must be
        // injected as raw characters into the source text; both backends receive
        // byte-identical in-memory source. Excludes " \ and newlines, which would
        // terminate/confuse the literal.
        if (_ctx.EnableUnicodeStrings && _ctx.Rng.NextDouble() < 0.25)
            return UnicodeLiteralBody();

        var pick = _ctx.Rng.NextDouble();
        if (pick < 0.1)
            return ""; // empty string
        if (pick < 0.2)
            return "\\n";
        if (pick < 0.3)
            return "\\t";
        if (pick < 0.4)
            return "\\r";
        if (pick < 0.5)
            return "\\\\";
        if (pick < 0.6)
            return "\\\"";

        // Otherwise a short ASCII alpha literal (2-6 chars).
        var len = 2 + _ctx.Rng.Next(5);
        var sb = new StringBuilder();
        for (var i = 0; i < len; i++)
        {
            // Stick to printable safe ASCII — letters + a few punctuation —
            // to avoid inadvertently producing characters that differ between
            // backends. The deliberate non-ASCII probe lives in UnicodeLiteralBody.
            var c = (char)('a' + _ctx.Rng.Next(26));
            sb.Append(c);
        }

        return sb.ToString();
    }

    // Raw non-ASCII literal body: a BMP non-ASCII char, a non-BMP surrogate pair
    // (String.Length == 2, observable via the string-length reducer), or a control
    // char. Deliberately exercises the source-encoding path on both backends.
    private string UnicodeLiteralBody()
    {
        return _ctx.Rng.Next(3) switch
        {
            // BMP non-ASCII (single UTF-16 code unit).
            0 => new[] { "é", "λ", "中", "Ω", "ñ" }[_ctx.Rng.Next(5)],
            // Non-BMP surrogate pairs (astral plane): emoji / mathematical bold.
            1 => char.ConvertFromUtf32(new[] { 0x1F600, 0x1F4A9, 0x1D400, 0x10348 }[_ctx.Rng.Next(4)]),
            // Control chars, excluding \n (0x0A) \r (0x0D) " \ — safe raw in a literal.
            _ => ((char)new[] { 0x01, 0x02, 0x07, 0x1B, 0x7F }[_ctx.Rng.Next(5)]).ToString(),
        };
    }
}
