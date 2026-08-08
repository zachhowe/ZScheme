using System.Runtime.CompilerServices;
using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

/// <summary>
///     ZS0004 — "this namespace qualifier is redundant". The suggestion is only correct when the
///     short spelling would resolve to the very same CLR type, so most of these tests are
///     negative: they pin the cases where shortening would change meaning or where the
///     justification is not visible in the file.
/// </summary>
public sealed class RedundantTypeQualifierTests
{
    private static IReadOnlyList<Diagnostic> Hints(
        string source,
        [CallerMemberName] string testName = ""
    )
    {
        var (svc, uri) = LspTestSession.Open(source, testName: testName);
        return
        [
            .. svc.GetDocument(uri)!
                .Diagnostics.Diagnostics.Where(d =>
                    d.Code == DiagnosticCodes.RedundantTypeQualifier
                ),
        ];
    }

    [Fact]
    public void QualifiedName_WithOwnImportClrNamespace_ReportsHintOverThePrefixOnly()
    {
        var src = """
            (module test)
            (import-clr System.Text)
            (define (grow [b : System.Text.StringBuilder]) b)
            """;
        var hint = Assert.Single(Hints(src));

        Assert.Equal(DiagnosticSeverity.Hint, hint.Severity);
        Assert.Equal(["StringBuilder", "System.Text"], hint.Data);
        Assert.Contains("can be written as 'StringBuilder'", hint.Message);

        // The range covers exactly `System.Text.`, so deleting it is the whole fix.
        var (line, col) = LspTestSession.Locate(src, "System.Text.StringBuilder");
        Assert.Equal(line, hint.Span.Line);
        Assert.Equal(col, hint.Span.Column);
        Assert.Equal("System.Text.".Length, hint.Span.Length);

        var related = Assert.Single(hint.Related!);
        var (nsLine, nsCol) = LspTestSession.Locate(src, "System.Text)");
        Assert.Equal(nsLine, related.Span.Line);
        Assert.Equal(nsCol, related.Span.Column);
    }

    [Fact]
    public void ReturnTypeAndNewExpression_AreReported()
    {
        var src = """
            (module test)
            (import-clr System.Text)
            (define (make) : System.Text.StringBuilder (new System.Text.StringBuilder))
            """;

        Assert.Equal(2, Hints(src).Count);
    }

    [Fact]
    public void ShortName_IsNotReported()
    {
        var src = """
            (module test)
            (import-clr System.Text)
            (define (grow [b : StringBuilder]) b)
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void NoOwnImportClrNamespace_ProducesNoHints()
    {
        var src = """
            (module test)
            (define (grow [b : System.Text.StringBuilder]) b)
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void ImportClrMemberPath_IsNotReported_ButItsSignatureIs()
    {
        var src = """
            (module test)
            (import-clr
              System.Text
              [sb-append System.Text.StringBuilder.Append
                :instance : (System.Text.StringBuilder String -> System.Text.StringBuilder)])
            """;
        var hints = Hints(src);

        Assert.Equal(2, hints.Count);
        // Both are in the signature on the last line; the member path on the line above is
        // resolved by ClrInterop without namespace hints and must keep its full spelling.
        Assert.All(hints, h => Assert.Equal(5, h.Span.Line));
    }

    [Fact]
    public void PrimitiveShortName_IsNotReported()
    {
        // `String` parses as a primitive type, not a named one, so this is not a rename —
        // it would change the annotation's ZType.
        var src = """
            (module test)
            (import-clr System)
            (define (len [s : System.String]) : Int 0)
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void SystemObjectAndTask_AreNotReported()
    {
        var src = """
            (module test)
            (import-clr System System.Threading.Tasks)
            (define (f [o : System.Object]) : (System.Threading.Tasks.Task Int) (g o))
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void UserDeclaredTypeWithTheSameShortName_IsNotReported()
    {
        var src = """
            (module test)
            (import-clr System.Text)
            (define-record StringBuilder [n : Int])
            (define (grow [b : System.Text.StringBuilder]) b)
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void UnresolvableNamespace_IsNotReported()
    {
        var src = """
            (module test)
            (import-clr Definitely.Does.Not.Exist)
            (define (f [x : Definitely.Does.Not.Exist.Widget]) x)
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void GenericType_UsesTypeArgumentArity()
    {
        // Resolving `Dictionary` at arity 2 means probing `Dictionary`2`; at arity 0 it would
        // miss and no hint would be offered.
        var src = """
            (module test)
            (import-clr System.Collections.Generic)
            (define (f [d : (System.Collections.Generic.Dictionary String Int)]) d)
            """;
        var hint = Assert.Single(Hints(src));

        Assert.Equal(["Dictionary", "System.Collections.Generic"], hint.Data);
        Assert.Equal("System.Collections.Generic.".Length, hint.Span.Length);
    }

    [Fact]
    public void ClrTypeShadowedByAZSchemeOne_IsNotReported()
    {
        // ZScheme's own `List` is in scope through the prelude, so writing `List` here would
        // mean the immutable ZScheme list, not System.Collections.Generic.List.
        var src = """
            (module test)
            (import-clr System.Collections.Generic)
            (define (f [d : (System.Collections.Generic.List Int)]) d)
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void NullableAnnotation_FlagsThePrefixOnly()
    {
        var src = """
            (module test)
            (import-clr System.Text)
            (define (f [b : System.Text.StringBuilder?]) b)
            """;
        var hint = Assert.Single(Hints(src));

        Assert.Equal("System.Text.".Length, hint.Span.Length);
    }

    [Fact]
    public void NamespaceInheritedFromAnImportedModule_IsNotReported()
    {
        // The canonicalizer would accept the short spelling here, but nothing in b.zs says why,
        // and dropping a.zs's import-clr would break it.
        using var ws = new TempPackageWorkspace(
            "qual",
            new Dictionary<string, string>
            {
                ["a.zs"] = """
                (module qual/a)
                (import-clr System.Text)
                (export a-marker)
                (define (a-marker) 1)
                """,
                ["b.zs"] = """
                (module qual/b)
                (import qual/a)
                (define (grow [b : System.Text.StringBuilder]) b)
                """,
            }
        );

        var state = ws.Open("b.zs");

        Assert.DoesNotContain(
            state.Diagnostics.Diagnostics,
            d => d.Code == DiagnosticCodes.RedundantTypeQualifier
        );
    }
}
