using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Analysis;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace ZScheme.LanguageServer.Tests;

public class FormattingEditsTests
{
    /// <summary>Applies edits the way a client would, so every test asserts the edits actually
    ///     reconstruct the formatted text rather than merely looking plausible.</summary>
    private static string Apply(string source, IReadOnlyList<TextEdit> edits)
    {
        // Later edits first: earlier ranges keep their offsets that way.
        foreach (var edit in edits.OrderByDescending(e => e.Range.Start.Line))
        {
            var start = SourceText.OffsetAt(
                source,
                edit.Range.Start.Line,
                edit.Range.Start.Character
            );
            var end = SourceText.OffsetAt(source, edit.Range.End.Line, edit.Range.End.Character);
            source = source[..start] + edit.NewText + source[end..];
        }

        return source;
    }

    private static void AssertRoundTrips(string before, string after)
    {
        var edits = FormattingEdits.Compute(before, after);
        Assert.Equal(after, Apply(before, edits));
    }

    [Fact]
    public void IdenticalText_NoEdits()
    {
        Assert.Empty(FormattingEdits.Compute("(define x 1)\n", "(define x 1)\n"));
    }

    [Fact]
    public void SingleChangedLine_OneNarrowEdit()
    {
        var before = "(define a 1)\n(define    b 2)\n(define c 3)\n";
        var after = "(define a 1)\n(define b 2)\n(define c 3)\n";

        var edit = Assert.Single(FormattingEdits.Compute(before, after));
        Assert.Equal(1, edit.Range.Start.Line);
        Assert.Equal(2, edit.Range.End.Line);
        Assert.Equal("(define b 2)\n", edit.NewText);
        AssertRoundTrips(before, after);
    }

    [Fact]
    public void InsertedLine_ZeroWidthEdit()
    {
        var before = "(define a 1)\n(define c 3)\n";
        var after = "(define a 1)\n(define b 2)\n(define c 3)\n";

        var edit = Assert.Single(FormattingEdits.Compute(before, after));
        Assert.Equal(edit.Range.Start, edit.Range.End);
        Assert.Equal("(define b 2)\n", edit.NewText);
        AssertRoundTrips(before, after);
    }

    [Fact]
    public void DeletedLine_EmptyNewText()
    {
        var before = "(define a 1)\n\n\n(define b 2)\n";
        var after = "(define a 1)\n\n(define b 2)\n";

        var edit = Assert.Single(FormattingEdits.Compute(before, after));
        Assert.Equal("", edit.NewText);
        AssertRoundTrips(before, after);
    }

    [Fact]
    public void SeparateChanges_ProduceSeparateHunks()
    {
        var before = "(a)\n(  b  )\n(c)\n(d)\n(  e  )\n(f)\n";
        var after = "(a)\n(b)\n(c)\n(d)\n(e)\n(f)\n";

        Assert.Equal(2, FormattingEdits.Compute(before, after).Count);
        AssertRoundTrips(before, after);
    }

    [Fact]
    public void FinalNewlineAdded_EditReachesDocumentEnd()
    {
        var before = "(define a 1)\n(define b 2)";
        var after = "(define a 1)\n(define b 2)\n";

        var edit = Assert.Single(FormattingEdits.Compute(before, after));
        Assert.Equal(new Position(1, 12), edit.Range.End);
        AssertRoundTrips(before, after);
    }

    [Fact]
    public void LineAppendedAfterUnterminatedLastLine_RoundTrips()
    {
        AssertRoundTrips("(a)\n(b)", "(a)\n(b)\n(c)\n");
    }

    [Fact]
    public void WholesaleReformat_FallsBackToOneReplace()
    {
        // Past the LCS cap every line differs, which is exactly when a single replace is right.
        var before = string.Concat(Enumerable.Range(0, 1500).Select(i => $"( a{i} )\n"));
        var after = string.Concat(Enumerable.Range(0, 1500).Select(i => $"(a{i})\n"));

        Assert.Single(FormattingEdits.Compute(before, after));
        AssertRoundTrips(before, after);
    }

    [Fact]
    public void Restriction_KeepsOnlyOverlappingHunks()
    {
        var before = "(  a  )\n(b)\n(  c  )\n";
        var after = "(a)\n(b)\n(c)\n";

        var edit = Assert.Single(
            FormattingEdits.Compute(
                before,
                after,
                new Range(new Position(0, 0), new Position(0, 7))
            )
        );
        Assert.Equal("(a)\n", edit.NewText);
    }

    [Fact]
    public void Restriction_SelectionEndingAtColumnZero_ExcludesThatLine()
    {
        var before = "(  a  )\n(  b  )\n";
        var after = "(a)\n(b)\n";

        // A selection of "line 0 only" is expressed by clients as 0:0 → 1:0.
        var edit = Assert.Single(
            FormattingEdits.Compute(
                before,
                after,
                new Range(new Position(0, 0), new Position(1, 0))
            )
        );
        Assert.Equal("(a)\n", edit.NewText);
    }

    [Fact]
    public void Restriction_InsertionAtSelectionStart_IsKept()
    {
        var before = "(a)\n(c)\n";
        var after = "(a)\n(b)\n(c)\n";

        Assert.Single(
            FormattingEdits.Compute(
                before,
                after,
                new Range(new Position(1, 0), new Position(1, 3))
            )
        );
    }
}
