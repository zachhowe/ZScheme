using Xunit;
using ZScheme.Compiler.Analysis;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Tests.Analysis;

/// <summary>
///     The automatic fixes are all straight rewrites of a diagnostic's own span — a deletion
///     for ZS0004, a replacement with the diagnostic's <c>Data[1]</c> for ZS0006 and ZS0007 —
///     so these pin the things that can go wrong when several are applied to one file: splice
///     order, the code scope, and everything outside the rewritten ranges surviving untouched.
/// </summary>
public sealed class DiagnosticFixerTests
{
    [Fact]
    public void FixableCodes_IsExactlyTheSpanRewriteContractCodes()
    {
        Assert.Equal(
            [
                DiagnosticCodes.RedundantTypeQualifier,
                DiagnosticCodes.DeprecatedAccessorSyntax,
                DiagnosticCodes.DeprecatedKeyword,
            ],
            DiagnosticFixer.FixableCodes.OrderBy(c => c).ToArray()
        );
    }

    /// <summary>A ZS0004 hint spanning the <c>prefix.</c> that starts at
    ///     <paramref name="occurrence" /> in <paramref name="source" /> — built the way the
    ///     analyzer builds it, from the 1-based line/column of the qualified name.</summary>
    private static Diagnostic QualifierHint(string source, string occurrence, string prefix)
    {
        var index = source.IndexOf(occurrence, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{occurrence}' not found in source");
        return HintAt(source, index, prefix.Length + 1, DiagnosticCodes.RedundantTypeQualifier);
    }

    /// <summary>A ZS0006/ZS0007 hint spanning <paramref name="atom" /> in
    ///     <paramref name="source" />, carrying <c>[atom, replacement]</c> the way the
    ///     emitters carry the modern spelling in <c>Data[1]</c>.</summary>
    private static Diagnostic DeprecationHint(
        string source,
        string atom,
        string code,
        string? replacement = null
    )
    {
        var index = source.IndexOf(atom, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{atom}' not found in source");
        return HintAt(
            source,
            index,
            atom.Length,
            code,
            replacement is null ? null : [atom, replacement]
        );
    }

    private static Diagnostic HintAt(
        string source,
        int index,
        int length,
        string code,
        IReadOnlyList<string>? data = null
    )
    {
        var (line, column) = PositionAt(source, index);
        return new Diagnostic(
            DiagnosticSeverity.Warning,
            "deprecated spelling",
            new SourceSpan("test.zs", line, column, length)
        )
        {
            Code = code,
            Data = data,
        };
    }

    private static (int Line, int Column) PositionAt(string source, int index)
    {
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < index; i++)
            if (source[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }

        return (line, index - lineStart + 1);
    }

    [Fact]
    public void SingleQualifierHint_DeletesExactlyThePrefix()
    {
        const string source = """
            (module test)
            (import-clr System.Text)
            (define (grow [b : System.Text.StringBuilder]) b)
            """;

        var (text, applied) = DiagnosticFixer.Apply(
            source,
            [QualifierHint(source, "System.Text.StringBuilder", "System.Text")]
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

        var (text, applied) = DiagnosticFixer.Apply(
            source,
            [
                HintAt(
                    source,
                    first,
                    "System.Text.".Length,
                    DiagnosticCodes.RedundantTypeQualifier
                ),
                HintAt(
                    source,
                    second,
                    "System.Text.".Length,
                    DiagnosticCodes.RedundantTypeQualifier
                ),
            ]
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

        var (text, _) = DiagnosticFixer.Apply(
            source,
            [
                HintAt(
                    source,
                    second,
                    "System.Text.".Length,
                    DiagnosticCodes.RedundantTypeQualifier
                ),
                HintAt(
                    source,
                    first,
                    "System.Text.".Length,
                    DiagnosticCodes.RedundantTypeQualifier
                ),
            ]
        );

        Assert.Equal("(define (grow [b : StringBuilder]) : StringBuilder b)", text);
    }

    [Fact]
    public void ZS0006_ReplacesTheLegacyAccessorWithTheModernSpelling()
    {
        const string source = "(module test)\n(define (code r) (HttpResponse/status-code r))\n";

        var (text, applied) = DiagnosticFixer.Apply(
            source,
            [
                DeprecationHint(
                    source,
                    "HttpResponse/status-code",
                    DiagnosticCodes.DeprecatedAccessorSyntax,
                    "HttpResponse-status-code"
                ),
            ]
        );

        Assert.Equal(1, applied);
        Assert.Equal("(module test)\n(define (code r) (HttpResponse-status-code r))\n", text);
    }

    [Fact]
    public void ZS0007_ReplacesTheDeprecatedHeadWithTheModernHead()
    {
        const string source = "(module test)\n(export add)\n";

        var (text, applied) = DiagnosticFixer.Apply(
            source,
            [DeprecationHint(source, "export", DiagnosticCodes.DeprecatedKeyword, "provide")]
        );

        Assert.Equal(1, applied);
        Assert.Equal("(module test)\n(provide add)\n", text);
    }

    /// <summary>A deletion and a replacement on the same line, in reverse source order — the
    ///     splice must hold for mixed codes too.</summary>
    [Fact]
    public void MixedCodesOnOneLine_BothApplyCorrectly()
    {
        const string source = "(f : System.Text.StringBuilder (HttpResponse/status-code r))";

        var qualifier = QualifierHint(source, "System.Text.StringBuilder", "System.Text");
        var accessor = DeprecationHint(
            source,
            "HttpResponse/status-code",
            DiagnosticCodes.DeprecatedAccessorSyntax,
            "HttpResponse-status-code"
        );

        var (text, applied) = DiagnosticFixer.Apply(source, [accessor, qualifier]);

        Assert.Equal(2, applied);
        Assert.Equal("(f : StringBuilder (HttpResponse-status-code r))", text);
    }

    [Fact]
    public void MixedCodesAcrossLines_AllApplyInSourceOrder()
    {
        const string source =
            "(module test)\n"
            + "(define (code r) (HttpResponse/status-code r))\n"
            + "(export code)\n";

        var accessor = DeprecationHint(
            source,
            "HttpResponse/status-code",
            DiagnosticCodes.DeprecatedAccessorSyntax,
            "HttpResponse-status-code"
        );
        var head = DeprecationHint(source, "export", DiagnosticCodes.DeprecatedKeyword, "provide");

        var (text, applied) = DiagnosticFixer.Apply(source, [head, accessor]);

        Assert.Equal(2, applied);
        Assert.Equal(
            "(module test)\n"
                + "(define (code r) (HttpResponse-status-code r))\n"
                + "(provide code)\n",
            text
        );
    }

    [Fact]
    public void OnlyCodes_RestrictsWhichFixesApply()
    {
        const string source =
            "(module test)\n(define (f : System.Text.StringBuilder x) (HttpResponse/status-code x))\n";

        var qualifier = QualifierHint(source, "System.Text.StringBuilder", "System.Text");
        var accessor = DeprecationHint(
            source,
            "HttpResponse/status-code",
            DiagnosticCodes.DeprecatedAccessorSyntax,
            "HttpResponse-status-code"
        );

        var (text, applied) = DiagnosticFixer.Apply(
            source,
            [qualifier, accessor],
            new HashSet<string>(
                [DiagnosticCodes.DeprecatedAccessorSyntax],
                StringComparer.OrdinalIgnoreCase
            )
        );

        Assert.Equal(1, applied);
        Assert.Equal(
            "(module test)\n(define (f : System.Text.StringBuilder x) (HttpResponse-status-code x))\n",
            text
        );

        // The scope is matched with the registry's comparer, so a typed lowercase code
        // selects the same fix.
        var (lowercaseText, lowercaseApplied) = DiagnosticFixer.Apply(
            source,
            [qualifier, accessor],
            new HashSet<string>(["zs0006"], StringComparer.OrdinalIgnoreCase)
        );

        Assert.Equal(applied, lowercaseApplied);
        Assert.Equal(text, lowercaseText);
    }

    [Fact]
    public void ZS0006WithoutAModernSpelling_IsDeclined()
    {
        const string source = "(module test)\n(define (code r) (HttpResponse/status-code r))\n";

        var incomplete = DeprecationHint(
            source,
            "HttpResponse/status-code",
            DiagnosticCodes.DeprecatedAccessorSyntax
        );

        var (text, applied) = DiagnosticFixer.Apply(source, [incomplete]);

        Assert.Equal(0, applied);
        Assert.Same(source, text);
    }

    [Fact]
    public void CrlfSource_KeepsItsLineEndings()
    {
        const string source =
            "(module test)\r\n(import-clr System.Text)\r\n(define (f) : System.Text.StringBuilder 1)\r\n";

        var (text, applied) = DiagnosticFixer.Apply(
            source,
            [QualifierHint(source, "System.Text.StringBuilder", "System.Text")]
        );

        Assert.Equal(1, applied);
        Assert.Equal(
            "(module test)\r\n(import-clr System.Text)\r\n(define (f) : StringBuilder 1)\r\n",
            text
        );
    }

    [Fact]
    public void CrlfSource_ReplacementsKeepTheirLineEndings()
    {
        const string source = "(module test)\r\n(define (code r) (HttpResponse/status-code r))\r\n";

        var (text, applied) = DiagnosticFixer.Apply(
            source,
            [
                DeprecationHint(
                    source,
                    "HttpResponse/status-code",
                    DiagnosticCodes.DeprecatedAccessorSyntax,
                    "HttpResponse-status-code"
                ),
            ]
        );

        Assert.Equal(1, applied);
        Assert.Equal("(module test)\r\n(define (code r) (HttpResponse-status-code r))\r\n", text);
    }

    [Fact]
    public void FileWithoutTrailingNewline_DoesNotGainOne()
    {
        const string source = "(module test)\n(define (f) : System.Text.StringBuilder 1)";

        var (text, _) = DiagnosticFixer.Apply(
            source,
            [QualifierHint(source, "System.Text.StringBuilder", "System.Text")]
        );

        Assert.Equal("(module test)\n(define (f) : StringBuilder 1)", text);
        Assert.False(text.EndsWith('\n'));
    }

    [Fact]
    public void HintOnTheLastLineWithNoTerminator_IsApplied()
    {
        const string source = "(module test)\n(f System.Text.StringBuilder)";

        var (text, applied) = DiagnosticFixer.Apply(
            source,
            [QualifierHint(source, "System.Text.StringBuilder", "System.Text")]
        );

        Assert.Equal(1, applied);
        Assert.Equal("(module test)\n(f StringBuilder)", text);
    }

    [Fact]
    public void NoHints_ReturnsTheSourceUnchanged()
    {
        const string source = "(module test)\n";

        var (text, applied) = DiagnosticFixer.Apply(source, []);

        Assert.Equal(0, applied);
        Assert.Same(source, text);
    }

    /// <summary>Only the span-rewrite codes describe a span that is safe to rewrite outright;
    ///     anything else in the bag is left alone.</summary>
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

        var (text, applied) = DiagnosticFixer.Apply(source, [unrelated]);

        Assert.Equal(0, applied);
        Assert.Same(source, text);
    }

    /// <summary>A span that runs past its line's end is not describing this text — rewriting
    ///     on it would splice across the newline, so the fixer declines and the caller reports
    ///     the hint as unapplied.</summary>
    [Fact]
    public void SpanRunningPastTheEndOfItsLine_IsDeclined()
    {
        const string source = "(module test)\n(f X)\n";

        var overlong = new Diagnostic(
            DiagnosticSeverity.Warning,
            "bogus",
            new SourceSpan("test.zs", 2, 4, 40)
        )
        {
            Code = DiagnosticCodes.RedundantTypeQualifier,
        };

        var (text, applied) = DiagnosticFixer.Apply(source, [overlong]);

        Assert.Equal(0, applied);
        Assert.Same(source, text);
    }

    [Fact]
    public void SpanOnALineBeyondTheFile_IsDeclined()
    {
        const string source = "(module test)\n";

        var outOfRange = new Diagnostic(
            DiagnosticSeverity.Warning,
            "bogus",
            new SourceSpan("test.zs", 99, 1, 3)
        )
        {
            Code = DiagnosticCodes.DeprecatedAccessorSyntax,
            Data = ["a/b", "a-b"],
        };

        var (_, applied) = DiagnosticFixer.Apply(source, [outOfRange]);

        Assert.Equal(0, applied);
    }
}
