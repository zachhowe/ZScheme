using Xunit;
using ZScheme.Compiler.Analysis;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Tests.Analysis;

/// <summary>
///     The ZS0004 auto-fix is a deletion of the diagnostic's own span, so these pin the two
///     things that can go wrong when several are applied to one file: splice order, and
///     everything outside the deleted ranges surviving untouched.
/// </summary>
public sealed class RedundantTypeQualifierFixerTests
{
    /// <summary>A hint spanning the <c>prefix.</c> that starts at <paramref name="occurrence" />
    ///     in <paramref name="source" /> — built the way the analyzer builds it, from the
    ///     1-based line/column of the qualified name.</summary>
    private static Diagnostic Hint(string source, string occurrence, string prefix)
    {
        var index = source.IndexOf(occurrence, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{occurrence}' not found in source");
        return HintAt(source, index, prefix);
    }

    private static Diagnostic HintAt(string source, int index, string prefix)
    {
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < index; i++)
            if (source[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }

        return new Diagnostic(
            DiagnosticSeverity.Hint,
            $"'{prefix}.X' can be written as 'X'",
            new SourceSpan("test.zs", line, index - lineStart + 1, prefix.Length + 1)
        )
        {
            Code = DiagnosticCodes.RedundantTypeQualifier,
        };
    }

    [Fact]
    public void SingleHint_DeletesExactlyThePrefix()
    {
        const string source = """
            (module test)
            (import-clr System.Text)
            (define (grow [b : System.Text.StringBuilder]) b)
            """;

        var (text, applied) = RedundantTypeQualifierFixer.Apply(
            source,
            [Hint(source, "System.Text.StringBuilder", "System.Text")]
        );

        Assert.Equal(1, applied);
        Assert.Equal(
            """
            (module test)
            (import-clr System.Text)
            (define (grow [b : StringBuilder]) b)
            """,
            text
        );
    }

    /// <summary>Two on one line is the case a left-to-right splice would corrupt: applying the
    ///     first shifts every column after it.</summary>
    [Fact]
    public void TwoHintsOnOneLine_BothApplyCorrectly()
    {
        const string source =
            "(define (grow [b : System.Text.StringBuilder]) : System.Text.StringBuilder b)";

        var first = source.IndexOf("System.Text.StringBuilder", StringComparison.Ordinal);
        var second = source.IndexOf(
            "System.Text.StringBuilder",
            first + 1,
            StringComparison.Ordinal
        );

        var (text, applied) = RedundantTypeQualifierFixer.Apply(
            source,
            [HintAt(source, first, "System.Text"), HintAt(source, second, "System.Text")]
        );

        Assert.Equal(2, applied);
        Assert.Equal("(define (grow [b : StringBuilder]) : StringBuilder b)", text);
    }

    /// <summary>Hints are not required to arrive in source order — the fixer sorts.</summary>
    [Fact]
    public void HintsInReverseOrder_ApplyTheSame()
    {
        const string source =
            "(define (grow [b : System.Text.StringBuilder]) : System.Text.StringBuilder b)";

        var first = source.IndexOf("System.Text.StringBuilder", StringComparison.Ordinal);
        var second = source.IndexOf(
            "System.Text.StringBuilder",
            first + 1,
            StringComparison.Ordinal
        );

        var (text, _) = RedundantTypeQualifierFixer.Apply(
            source,
            [HintAt(source, second, "System.Text"), HintAt(source, first, "System.Text")]
        );

        Assert.Equal("(define (grow [b : StringBuilder]) : StringBuilder b)", text);
    }

    [Fact]
    public void CrlfSource_KeepsItsLineEndings()
    {
        const string source =
            "(module test)\r\n(import-clr System.Text)\r\n(define (f) : System.Text.StringBuilder 1)\r\n";

        var (text, applied) = RedundantTypeQualifierFixer.Apply(
            source,
            [Hint(source, "System.Text.StringBuilder", "System.Text")]
        );

        Assert.Equal(1, applied);
        Assert.Equal(
            "(module test)\r\n(import-clr System.Text)\r\n(define (f) : StringBuilder 1)\r\n",
            text
        );
    }

    [Fact]
    public void FileWithoutTrailingNewline_DoesNotGainOne()
    {
        const string source = "(module test)\n(define (f) : System.Text.StringBuilder 1)";

        var (text, _) = RedundantTypeQualifierFixer.Apply(
            source,
            [Hint(source, "System.Text.StringBuilder", "System.Text")]
        );

        Assert.Equal("(module test)\n(define (f) : StringBuilder 1)", text);
        Assert.False(text.EndsWith('\n'));
    }

    [Fact]
    public void HintOnTheLastLineWithNoTerminator_IsApplied()
    {
        const string source = "(module test)\n(f System.Text.StringBuilder)";

        var (text, applied) = RedundantTypeQualifierFixer.Apply(
            source,
            [Hint(source, "System.Text.StringBuilder", "System.Text")]
        );

        Assert.Equal(1, applied);
        Assert.Equal("(module test)\n(f StringBuilder)", text);
    }

    [Fact]
    public void NoHints_ReturnsTheSourceUnchanged()
    {
        const string source = "(module test)\n";

        var (text, applied) = RedundantTypeQualifierFixer.Apply(source, []);

        Assert.Equal(0, applied);
        Assert.Same(source, text);
    }

    /// <summary>Only ZS0004 describes a span that is safe to delete outright; anything else in
    ///     the bag is left alone.</summary>
    [Fact]
    public void DiagnosticsWithOtherCodes_AreIgnored()
    {
        const string source = "(module test)\n(define (f) : System.Text.StringBuilder 1)";

        var unrelated = new Diagnostic(
            DiagnosticSeverity.Warning,
            "Unused binding 'x'",
            new SourceSpan("test.zs", 2, 10, 5)
        )
        {
            Code = DiagnosticCodes.UnusedBinding,
        };

        var (text, applied) = RedundantTypeQualifierFixer.Apply(source, [unrelated]);

        Assert.Equal(0, applied);
        Assert.Same(source, text);
    }

    /// <summary>A span that runs past its line's end is not describing this text — deleting on
    ///     it would splice across the newline, so the fixer declines and the caller reports the
    ///     hint as unapplied.</summary>
    [Fact]
    public void SpanRunningPastTheEndOfItsLine_IsDeclined()
    {
        const string source = "(module test)\n(f X)\n";

        var overlong = new Diagnostic(
            DiagnosticSeverity.Hint,
            "bogus",
            new SourceSpan("test.zs", 2, 4, 40)
        )
        {
            Code = DiagnosticCodes.RedundantTypeQualifier,
        };

        var (text, applied) = RedundantTypeQualifierFixer.Apply(source, [overlong]);

        Assert.Equal(0, applied);
        Assert.Same(source, text);
    }

    [Fact]
    public void SpanOnALineBeyondTheFile_IsDeclined()
    {
        const string source = "(module test)\n";

        var outOfRange = new Diagnostic(
            DiagnosticSeverity.Hint,
            "bogus",
            new SourceSpan("test.zs", 99, 1, 3)
        )
        {
            Code = DiagnosticCodes.RedundantTypeQualifier,
        };

        var (_, applied) = RedundantTypeQualifierFixer.Apply(source, [outOfRange]);

        Assert.Equal(0, applied);
    }
}
