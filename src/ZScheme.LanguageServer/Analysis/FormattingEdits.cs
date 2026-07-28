using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Turns a whole-document reformat into the smallest set of line-granular
///     <see cref="TextEdit" />s that produces it, optionally keeping only the ones that touch a
///     requested range.
///     <para>
///         Replacing the entire buffer would be simpler but costs the user their folds and
///         scroll position in several clients, and would make every format look like a
///         whole-file change to source control views.
///     </para>
///     <para>
///         Range formatting is expressed as the same full-document diff with non-overlapping
///         hunks dropped, rather than as a separate "format just this text" path. That makes it
///         correct by construction: a selection can never be laid out differently from how a
///         full format would lay it out, and no partial re-parse of a fragment is involved.
///     </para>
/// </summary>
internal static class FormattingEdits
{
    /// <summary>
    ///     Above this many differing lines on either side the LCS table stops being worth its
    ///     memory, and a wholesale reformat is exactly the case where a single replace is the
    ///     right answer anyway.
    /// </summary>
    private const int MaxDiffLines = 1000;

    public static IReadOnlyList<TextEdit> Compute(
        string original,
        string formatted,
        Range? restrictTo = null
    )
    {
        if (original == formatted)
            return [];

        var before = SplitLines(original);
        var after = SplitLines(formatted);

        var hunks = Diff(before, after);
        if (restrictTo is not null)
            hunks = [.. Split(hunks).Where(h => Intersects(h, restrictTo))];

        return [.. hunks.Select(h => ToEdit(h, before, after))];
    }

    /// <summary>A hunk replaces original lines <c>[OrigStart, OrigEnd)</c> with formatted
    ///     lines <c>[NewStart, NewEnd)</c>. Either side may be empty (insertion / deletion).</summary>
    private readonly record struct Hunk(int OrigStart, int OrigEnd, int NewStart, int NewEnd);

    /// <summary>Splits into lines that each keep their own trailing newline, so concatenating
    ///     any slice reproduces that region of the text verbatim. A text ending in a newline
    ///     therefore yields no trailing empty element — the final newline belongs to the last
    ///     line.</summary>
    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
            return [];

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }

        if (start < text.Length)
            lines.Add(text[start..]);

        return [.. lines];
    }

    private static List<Hunk> Diff(string[] before, string[] after)
    {
        // Trim the matching head and tail; formatting usually rewrites a handful of interior
        // lines, so this alone reduces most documents to a tiny differing region.
        var prefix = 0;
        var min = Math.Min(before.Length, after.Length);
        while (prefix < min && before[prefix] == after[prefix])
            prefix++;

        var suffix = 0;
        while (
            suffix < min - prefix
            && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]
        )
            suffix++;

        var beforeEnd = before.Length - suffix;
        var afterEnd = after.Length - suffix;

        if (prefix >= beforeEnd && prefix >= afterEnd)
            return [];

        if (beforeEnd - prefix > MaxDiffLines || afterEnd - prefix > MaxDiffLines)
            return [new Hunk(prefix, beforeEnd, prefix, afterEnd)];

        return HunksFromLcs(before, after, prefix, beforeEnd, afterEnd);
    }

    private static List<Hunk> HunksFromLcs(
        string[] before,
        string[] after,
        int prefix,
        int beforeEnd,
        int afterEnd
    )
    {
        var m = beforeEnd - prefix;
        var n = afterEnd - prefix;

        var lcs = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
        for (var j = n - 1; j >= 0; j--)
            lcs[i, j] =
                before[prefix + i] == after[prefix + j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var hunks = new List<Hunk>();
        var origRun = prefix;
        var newRun = prefix;
        int x = 0,
            y = 0;

        void FlushRun(int origStop, int newStop)
        {
            if (origStop > origRun || newStop > newRun)
                hunks.Add(new Hunk(origRun, origStop, newRun, newStop));
        }

        while (x < m && y < n)
        {
            if (before[prefix + x] == after[prefix + y])
            {
                // A matching line closes whatever run of differences preceded it.
                FlushRun(prefix + x, prefix + y);
                x++;
                y++;
                origRun = prefix + x;
                newRun = prefix + y;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        FlushRun(beforeEnd, afterEnd);
        return hunks;
    }

    /// <summary>
    ///     Breaks each line-for-line hunk into one hunk per line, so a selection can be honoured
    ///     to the line. Two adjacent re-indented lines otherwise form a single hunk, and selecting
    ///     just one of them would drag the other along. Hunks that change the line count are
    ///     structural (a form joined onto one line, or split across several) and have no
    ///     line-to-line correspondence to split on, so they stay whole. Only worth doing for range
    ///     formatting — a full-document format wants the coarser edits.
    /// </summary>
    private static IEnumerable<Hunk> Split(List<Hunk> hunks)
    {
        foreach (var hunk in hunks)
        {
            var length = hunk.OrigEnd - hunk.OrigStart;
            if (length < 2 || length != hunk.NewEnd - hunk.NewStart)
            {
                yield return hunk;
                continue;
            }

            for (var i = 0; i < length; i++)
                yield return new Hunk(
                    hunk.OrigStart + i,
                    hunk.OrigStart + i + 1,
                    hunk.NewStart + i,
                    hunk.NewStart + i + 1
                );
        }
    }

    private static bool Intersects(Hunk hunk, Range range)
    {
        var startLine = range.Start.Line;

        // A selection that ends at column 0 of a line does not actually cover that line —
        // that is how editors express "up to the end of the previous line".
        var endLine = range.End.Line;
        if (range.End.Character == 0 && endLine > startLine)
            endLine--;

        // Pure insertions are zero-width in the original: they sit *at* OrigStart.
        if (hunk.OrigEnd == hunk.OrigStart)
            return hunk.OrigStart >= startLine && hunk.OrigStart <= endLine;

        return hunk.OrigStart <= endLine && hunk.OrigEnd > startLine;
    }

    private static TextEdit ToEdit(Hunk hunk, string[] before, string[] after)
    {
        return new TextEdit
        {
            Range = new Range(
                new Position(hunk.OrigStart, 0),
                LineStartOrDocumentEnd(before, hunk.OrigEnd)
            ),
            NewText = string.Concat(after[hunk.NewStart..hunk.NewEnd]),
        };
    }

    /// <summary>The start of line <paramref name="line" />, or the end of the document when
    ///     that line is one past the last — which is where a hunk reaching the end lands, and
    ///     the only case where the position is not column 0.</summary>
    private static Position LineStartOrDocumentEnd(string[] lines, int line)
    {
        if (line < lines.Length)
            return new Position(line, 0);
        if (lines.Length == 0)
            return new Position(0, 0);

        var last = lines[^1];
        return last.EndsWith('\n')
            ? new Position(lines.Length, 0)
            : new Position(lines.Length - 1, last.Length);
    }
}
