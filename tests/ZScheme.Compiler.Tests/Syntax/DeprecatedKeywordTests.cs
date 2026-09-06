using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Syntax;

/// <summary>
///     The deprecated special-form heads still build, and say so (ZS0007) — <c>export</c>
///     for <c>provide</c>, and the <c>define-</c>-prefixed type declarations for their bare
///     replacements. See <see cref="KeywordAliases" />.
/// </summary>
public class DeprecatedKeywordTests
{
    private static (AstNode.Program program, DiagnosticBag diag) Build(
        string source,
        bool warn = true
    )
    {
        var diag = new DiagnosticBag();
        var tokens = new Lexer(source, "test.zs", diag).Tokenize();
        var sexprs = new SExprParser(tokens, diag).ParseAll();
        var program = new AstBuilder(diag, warn).BuildProgram(sexprs);
        return (program, diag);
    }

    private static Diagnostic[] KeywordWarnings(DiagnosticBag diag) =>
        diag.Diagnostics.Where(d => d.Code == DiagnosticCodes.DeprecatedKeyword).ToArray();

    /// <summary>Every rename, as legacy head / modern head / a source using each.</summary>
    public static TheoryData<string, string, string, string> Renames() =>
        new()
        {
            { "export", "provide", "(export foo)", "(provide foo)" },
            {
                "define-record",
                "record",
                "(define-record Point [x : Int])",
                "(record Point [x : Int])"
            },
            {
                "define-struct",
                "struct",
                "(define-struct Vec [x : Int])",
                "(struct Vec [x : Int])"
            },
            {
                "define-union",
                "union",
                "(define-union Shape (Circle [r : Int]))",
                "(union Shape (Circle [r : Int]))"
            },
            {
                "define-class",
                "class",
                "(define-class Dog [name : String])",
                "(class Dog [name : String])"
            },
            {
                "define-interface",
                "interface",
                "(define-interface Speaker (Speak [] : String))",
                "(interface Speaker (Speak [] : String))"
            },
        };

    // ---- The modern head is the one to write, and it builds the same node ----

    [Theory]
    [MemberData(nameof(Renames))]
    public void ModernHead_BuildsCleanly_AndMatchesTheLegacyHead(
        string legacyHead,
        string modernHead,
        string legacySource,
        string modernSource
    )
    {
        var (modernProgram, modernDiag) = Build(modernSource);
        Assert.False(modernDiag.HasErrors);
        Assert.Empty(KeywordWarnings(modernDiag));

        var (legacyProgram, legacyDiag) = Build(legacySource);
        Assert.False(legacyDiag.HasErrors);

        // Same form, so the same node type — the head spelling is the only difference.
        var modernForm = Assert.Single(modernProgram.TopLevelForms);
        var legacyForm = Assert.Single(legacyProgram.TopLevelForms);
        Assert.Equal(modernForm.GetType(), legacyForm.GetType());
        Assert.NotEqual(legacyHead, modernHead);
    }

    // ---- The legacy head still builds, and warns ----

    [Theory]
    [MemberData(nameof(Renames))]
    public void LegacyHead_StillBuilds_AndWarns(
        string legacyHead,
        string modernHead,
        string legacySource,
        string modernSource
    )
    {
        _ = modernSource;

        var (_, diag) = Build(legacySource);

        var warning = Assert.Single(KeywordWarnings(diag));
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal([legacyHead, modernHead], warning.Data);
        Assert.Contains(modernHead, warning.Message);
        Assert.False(diag.HasErrors);
    }

    [Theory]
    [MemberData(nameof(Renames))]
    public void LegacyHead_WarningSpansOnlyTheHeadAtom(
        string legacyHead,
        string modernHead,
        string legacySource,
        string modernSource
    )
    {
        _ = modernHead;
        _ = modernSource;

        var (_, diag) = Build(legacySource);

        // The quick fix is a straight replacement of the span, so the span has to cover the
        // head atom and nothing else.
        var warning = Assert.Single(KeywordWarnings(diag));
        var line = legacySource.Split('\n')[warning.Span.Line - 1];
        Assert.Equal(legacyHead, line.Substring(warning.Span.Column - 1, warning.Span.Length));
    }

    [Fact]
    public void LegacyHeads_WarnOncePerOccurrence()
    {
        var (_, diag) = Build(
            """
            (define-record Point [x : Int])
            (define-record Other [y : Int])
            (export foo)
            """
        );

        Assert.Equal(3, KeywordWarnings(diag).Length);
    }

    [Fact]
    public void LegacyHead_IsSilentWhenSuppressed_AndStillBuilds()
    {
        var (program, diag) = Build("(define-record Point [x : Int])", warn: false);

        Assert.Empty(KeywordWarnings(diag));
        Assert.False(diag.HasErrors);
        Assert.IsType<AstNode.RecordDecl>(Assert.Single(program.TopLevelForms));
    }

    // ---- Shape errors name the modern head ----

    [Theory]
    [InlineData("(provide)", "'provide' requires at least one name")]
    [InlineData("(export)", "'provide' requires at least one name")]
    [InlineData("(provide (foo))", "'provide' entries must be names")]
    [InlineData("(record)", "'record' requires a name")]
    [InlineData("(define-record)", "'record' requires a name")]
    [InlineData("(struct)", "'struct' requires a name")]
    [InlineData("(define-struct)", "'struct' requires a name")]
    public void ShapeError_NamesTheModernHead(string source, string expected)
    {
        var (_, diag) = Build(source);

        Assert.Contains(
            diag.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains(expected)
        );
    }

    // ---- Both spellings are still rejected inside a body ----

    [Theory]
    [InlineData("record")]
    [InlineData("define-record")]
    [InlineData("union")]
    [InlineData("define-union")]
    [InlineData("class")]
    [InlineData("define-class")]
    [InlineData("interface")]
    [InlineData("define-interface")]
    public void TypeDeclaration_InABody_IsRejectedUnderEitherSpelling(string head)
    {
        var (_, diag) = Build($"(define (f) : Int (begin ({head} Foo [x : Int]) 1))");

        Assert.Contains(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Error
                && d.Message.Contains("only allowed at the top level")
        );
    }

    // ---- KeywordAliases itself ----

    [Theory]
    [InlineData("export", "provide")]
    [InlineData("define-record", "record")]
    [InlineData("define-struct", "struct")]
    [InlineData("define-union", "union")]
    [InlineData("define-class", "class")]
    [InlineData("define-interface", "interface")]
    public void TryModernize_MapsEveryDeprecatedHead(string legacy, string modern)
    {
        Assert.Equal(modern, KeywordAliases.TryModernize(legacy));
    }

    [Theory]
    [InlineData("define")]
    [InlineData("define-async")]
    [InlineData("define-syntax")]
    [InlineData("define-type-alias")]
    [InlineData("provide")]
    [InlineData("record")]
    [InlineData("lambda")]
    public void TryModernize_LeavesEveryOtherHeadAlone(string head)
    {
        Assert.Null(KeywordAliases.TryModernize(head));
    }
}
