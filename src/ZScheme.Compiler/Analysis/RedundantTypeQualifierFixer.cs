using System.Text;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Analysis;

/// <summary>
///     Applies <see cref="DiagnosticCodes.RedundantTypeQualifier" /> (ZS0004) fixes to a
///     source file. The whole fix is a deletion: the diagnostic's span covers exactly the
///     redundant <c>Ns.</c> characters (see <see cref="RedundantTypeQualifierAnalyzer" />), so
///     cutting the span out leaves the short spelling behind — which is why the LSP quick fix
///     is a <c>NewText = ""</c> edit over the same range and this needs no AST.
///     <para>
///         Splices the raw string rather than splitting into lines, so line endings, the
///         presence or absence of a trailing newline, and everything outside the deleted ranges
///         survive byte-for-byte.
///     </para>
/// </summary>
public static class RedundantTypeQualifierFixer
{
    /// <summary>
    ///     <paramref name="source" /> with every ZS0004 hint's span deleted, plus how many were
    ///     applied. Non-ZS0004 diagnostics are ignored, as are spans that do not sit within a
    ///     single line of <paramref name="source" /> — deleting on a stale or mismatched span
    ///     would corrupt the file, and declining only costs a missed fix.
    /// </summary>
    public static (string Text, int Applied) Apply(string source, IReadOnlyList<Diagnostic> hints)
    {
        var deletions = Deletions(source, hints);
        if (deletions.Count == 0)
            return (source, 0);

        // Descending so each splice leaves the offsets of the ones still pending untouched.
        deletions.Sort((a, b) => b.Start.CompareTo(a.Start));

        var result = new StringBuilder(source);
        var applied = 0;
        var lastStart = source.Length;
        foreach (var (start, length) in deletions)
        {
            // The analyzer keys on the last dot of a name, so it emits at most one hint per
            // token and overlaps should not arise; if one ever did, applying both would delete
            // text neither hint claimed.
            if (start + length > lastStart)
                continue;
            result.Remove(start, length);
            lastStart = start;
            applied++;
        }

        return (result.ToString(), applied);
    }

    private static List<(int Start, int Length)> Deletions(
        string source,
        IReadOnlyList<Diagnostic> hints
    )
    {
        var deletions = new List<(int Start, int Length)>();
        if (hints.Count == 0)
            return deletions;

        var lineStarts = LineStarts(source);
        foreach (var hint in hints)
        {
            if (hint.Code != DiagnosticCodes.RedundantTypeQualifier)
                continue;

            var span = hint.Span;
            if (
                span.Length <= 0
                || span.Line < 1
                || span.Line > lineStarts.Count
                || span.Column < 1
            )
                continue;

            // A ZS0004 span is always intra-line, so it must end at or before this line's
            // terminator; one that runs past it is not describing this text.
            var start = lineStarts[span.Line - 1] + (span.Column - 1);
            if (start + span.Length > LineContentEnd(source, lineStarts, span.Line))
                continue;

            deletions.Add((start, span.Length));
        }

        return deletions;
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
