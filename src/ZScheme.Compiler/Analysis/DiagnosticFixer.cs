using System.Text;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Analysis;

/// <summary>
///     Applies the automatic fixes for every diagnostic whose fix is a straight rewrite of
///     the diagnostic's own span — the convention ZS0004, ZS0006 and ZS0007 share (see
///     <see cref="DiagnosticCodes" />): the span covers exactly the text to change, and the
///     replacement is either nothing (ZS0004, the redundant qualifier) or the diagnostic's
///     own <c>Data[1]</c> (ZS0006 and ZS0007, the modern spelling). That is the same edit the
///     LSP offers as a per-diagnostic quick fix, so <c>zs lint --fix</c> applies exactly what
///     the editor offers one at a time — just across a whole package at once.
///     <para>
///         Splices the raw string rather than splitting into lines, so line endings, the
///         presence or absence of a trailing newline, and everything outside the rewritten
///         ranges survive byte-for-byte.
///     </para>
/// </summary>
public static class DiagnosticFixer
{
    /// <summary>
    ///     The codes this fixer knows an edit for — the single source of truth that
    ///     <c>zs lint --fix</c> validates its code list against. Keyed case-insensitively so a
    ///     typed scope matches the canonical spellings.
    /// </summary>
    public static IReadOnlySet<string> FixableCodes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            DiagnosticCodes.RedundantTypeQualifier,
            DiagnosticCodes.DeprecatedAccessorSyntax,
            DiagnosticCodes.DeprecatedKeyword,
        };

    /// <summary>
    ///     <paramref name="source" /> with the fix applied to every fixable diagnostic in
    ///     <paramref name="hints" /> — restricted to <paramref name="onlyCodes" /> when that
    ///     is not null — plus how many were applied. Diagnostics without a known edit are
    ///     ignored, and so are spans that do not sit within a single line of
    ///     <paramref name="source" /> — rewriting on a stale or mismatched span would corrupt
    ///     the file, and declining only costs a missed fix.
    /// </summary>
    public static (string Text, int Applied) Apply(
        string source,
        IReadOnlyList<Diagnostic> hints,
        IReadOnlySet<string>? onlyCodes = null
    )
    {
        var edits = Edits(source, hints, onlyCodes);
        if (edits.Count == 0)
            return (source, 0);

        // Descending so each splice leaves the offsets of the ones still pending untouched.
        // On a start tie the longer span wins: it claims everything the shorter one does.
        edits.Sort(
            (a, b) =>
                b.Start.CompareTo(a.Start) != 0
                    ? b.Start.CompareTo(a.Start)
                    : b.Length.CompareTo(a.Length)
        );

        var result = new StringBuilder(source);
        var applied = 0;
        var lastStart = source.Length;
        foreach (var (start, length, text) in edits)
        {
            // The emitters key on whole tokens, so they emit at most one hint per token and
            // overlaps should not arise; if one ever did, applying both would rewrite text
            // neither hint claimed alone.
            if (start + length > lastStart)
                continue;
            // StringBuilder has no (start, length, replacement) overload: remove, then insert.
            result.Remove(start, length);
            if (text.Length > 0)
                result.Insert(start, text);
            lastStart = start;
            applied++;
        }

        return (result.ToString(), applied);
    }

    private static List<(int Start, int Length, string Text)> Edits(
        string source,
        IReadOnlyList<Diagnostic> hints,
        IReadOnlySet<string>? onlyCodes
    )
    {
        var edits = new List<(int Start, int Length, string Text)>();
        if (hints.Count == 0)
            return edits;

        var lineStarts = LineStarts(source);
        foreach (var hint in hints)
        {
            if (ReplacementFor(hint) is not { } replacement)
                continue;
            if (onlyCodes is not null && !onlyCodes.Contains(hint.Code!))
                continue;

            var span = hint.Span;
            if (
                span.Length <= 0
                || span.Line < 1
                || span.Line > lineStarts.Count
                || span.Column < 1
            )
                continue;

            // Every fixable span is intra-line, so it must end at or before this line's
            // terminator; one that runs past it is not describing this text.
            var start = lineStarts[span.Line - 1] + (span.Column - 1);
            if (start + span.Length > LineContentEnd(source, lineStarts, span.Line))
                continue;

            edits.Add((start, span.Length, replacement));
        }

        return edits;
    }

    /// <summary>
    ///     The text that replaces the diagnostic's own span — null when the code has no span
    ///     rewrite, or when the payload a rewrite needs is missing. ZS0004's fix is a
    ///     deletion; ZS0006 and ZS0007 carry their modern spelling in <c>Data[1]</c>.
    /// </summary>
    private static string? ReplacementFor(Diagnostic hint)
    {
        switch (hint.Code)
        {
            case DiagnosticCodes.RedundantTypeQualifier:
                return "";
            case DiagnosticCodes.DeprecatedAccessorSyntax:
            case DiagnosticCodes.DeprecatedKeyword:
                return hint.Data is [_, { Length: > 0 } modern] ? modern : null;
            default:
                return null;
        }
    }

    /// <summary>Offset just past the last content character of a 1-based line — the newline
    ///     sequence that ends it is excluded, CR and CRLF alike.</summary>
    private static int LineContentEnd(string source, List<int> lineStarts, int line)
    {
        if (line >= lineStarts.Count)
            return source.Length;

        // lineStarts[line] is one past this line's '\n'.
        var end = lineStarts[line] - 1;
        return end > 0 && source[end - 1] == '\r' ? end - 1 : end;
    }

    /// <summary>Offset of the first character of each 1-based line. Handles LF and CRLF alike:
    ///     a line starts right after its <c>\n</c>, so a preceding <c>\r</c> stays on the line it
    ///     terminates.</summary>
    private static List<int> LineStarts(string source)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++)
            if (source[i] == '\n')
                starts.Add(i + 1);

        return starts;
    }
}
