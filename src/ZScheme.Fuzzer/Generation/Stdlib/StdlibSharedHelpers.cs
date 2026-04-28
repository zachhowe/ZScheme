using System.Text;

namespace ZScheme.Fuzzer.Generation.Stdlib;

// Shared helpers used by multiple Stdlib<X>Generator types.
internal static class StdlibSharedHelpers
{
    // Builds a quoted 1-3 char lowercase-ASCII key literal. Safe alphabet means
    // no escape handling is needed inside the "..." form.
    public static string QuotedShortAsciiString(GeneratorContext ctx)
    {
        var len = 1 + ctx.Rng.Next(3); // 1..3 chars
        var sb = new StringBuilder("\"");
        for (var i = 0; i < len; i++)
            sb.Append((char)('a' + ctx.Rng.Next(26)));
        sb.Append('"');
        return sb.ToString();
    }
}
