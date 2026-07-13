using System.Text;

namespace ZScheme.Fuzzer.Generation;

// Emits String-typed expressions and reduces them to Int for the compute body.
// Covers StringConst emission (both backends), lexer escape handling (\n, \t, \r,
// \\, \"), and both spellings of concatenation — `string-append` and the string form
// of `+` — which share a left fold and lower to `System.String.Concat` in IL and `+`
// in C#. The Int reducer uses string equality `(= s1 s2)` → `(if ... 1 0)`,
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
    // escape sequences, or a String var from scope. Inner: `string-append` and the
    // string form of `+` (both fold to the same binary BinOp("+") chain).
    public string GenString(Scope scope, int depth)
    {
        if (depth <= 0)
            return GenStringLeaf(scope);

        var weights = new List<(int Weight, Func<string> Gen)>
        {
            (4, () => GenStringLeaf(scope)),
            (3, () => GenStringAppend(scope, depth)),
            (2, () => GenStringPlus(scope, depth)),
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

    // `string-append` is variadic (FoldKind.LeftFoldIdentity), so three shapes are
    // worth probing. ~25% emit a deliberately deep hand-nested left- or right-leaning
    // chain of 4-6 leaves — C# lowers each node to `+` while IL lowers to
    // String.Concat, so the nesting shape is an associativity/evaluation-order probe.
    // ~25% emit an n-ary call, which AstBuilder left-folds into that same binary
    // chain; emitting both shapes checks the fold agrees with hand-nesting. The
    // 1-arg identity form `(string-append x)` → `x` is folded in too.
    private string GenStringAppend(Scope scope, int depth)
    {
        var roll = _ctx.Rng.NextDouble();
        if (roll < 0.25)
            return GenNestedChain(scope, "string-append");
        if (roll < 0.5)
            return GenNaryCall(scope, depth, "string-append", minArgs: 1);

        var a = GenString(scope, depth - 1);
        var b = GenString(scope, depth - 1);
        return $"(string-append {a} {b})";
    }

    // The string form of `+`: same left fold and same BinOp("+") lowering as
    // string-append, reached through the arithmetic operator's constrained type var
    // instead. Worth its own probe because inference has to pin the operand kind to
    // String — if it defaulted to Int, the IL backend would emit `add` on object refs.
    private string GenStringPlus(Scope scope, int depth)
    {
        if (_ctx.Rng.NextDouble() < 0.35)
            return GenNestedChain(scope, "+");

        // Minimum 2 args: `(+ x)` on a String would be the arithmetic identity, which
        // is fine, but n-ary is what actually exercises the fold.
        return GenNaryCall(scope, depth, "+", minArgs: 2);
    }

    // (op a b c ...) with 1-6 operands, left-folded by AstBuilder.
    private string GenNaryCall(Scope scope, int depth, string op, int minArgs)
    {
        var count = minArgs + _ctx.Rng.Next(6 - minArgs + 1);
        var parts = new List<string>();
        for (var i = 0; i < count; i++)
            parts.Add(GenString(scope, depth - 1));
        return $"({op} {string.Join(' ', parts)})";
    }

    // A hand-nested binary chain of 4-6 leaves, leaning left or right.
    private string GenNestedChain(Scope scope, string op)
    {
        var leaves = 4 + _ctx.Rng.Next(3);
        var leftLeaning = _ctx.Rng.NextDouble() < 0.5;
        var acc = GenStringLeaf(scope);
        for (var i = 1; i < leaves; i++)
        {
            var next = GenStringLeaf(scope);
            acc = leftLeaning ? $"({op} {acc} {next})" : $"({op} {next} {acc})";
        }

        return acc;
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
