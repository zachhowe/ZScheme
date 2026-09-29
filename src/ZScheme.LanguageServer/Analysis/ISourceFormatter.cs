using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Seam between the <c>textDocument/formatting</c> family of handlers and a source
///     formatter. The real ZScheme formatter is in progress on another branch; until it
///     lands, the registered <see cref="NullFormatter" /> makes every formatting request
///     decline (null), so clients keep the user's text untouched. When the formatter
///     lands, it implements this interface and is swapped in via DI — the handlers
///     themselves need no changes. One known limitation of the seam: it carries no
///     trigger character or position, so on-type formatting
///     (<c>textDocument/onTypeFormatting</c>) cannot reach a real formatter through this
///     signature — the interface will need to grow to carry the trigger before on-type
///     support can land.
/// </summary>
public interface ISourceFormatter
{
    /// <summary>The edits that format <paramref name="source" /> — the whole document,
    ///     or only <paramref name="range" /> when a range is given (range-formatting
    ///     requests) — or null to decline. Returning an empty list is reserved for
    ///     "nothing to do"; implementations that cannot format must return null so the
    ///     server responds with a null result instead of an empty edit set.</summary>
    IReadOnlyList<TextEdit>? Format(string source, Range? range);
}

/// <summary>The default formatter until the real one lands: always declines.</summary>
public sealed class NullFormatter : ISourceFormatter
{
    public IReadOnlyList<TextEdit>? Format(string source, Range? range) => null;
}
