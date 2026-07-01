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
            // backends (non-BMP, control chars, etc.). That's a separate fuzz
            // target worth doing deliberately later.
            var c = (char)('a' + _ctx.Rng.Next(26));
            sb.Append(c);
        }

        return sb.ToString();
    }
}
