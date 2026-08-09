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
    public void ImportClrMemberPath_IsReportedAlongsideItsSignature()
    {
        var src = """
            (module test)
            (import-clr
              System.Text
              [sb-append System.Text.StringBuilder.Append
                :instance : (System.Text.StringBuilder String -> System.Text.StringBuilder)])
            """;
        var hints = Hints(src);

        Assert.Equal(3, hints.Count);
        // Two in the signature on the last line, one on the member path above it.
        var (line, col) = LspTestSession.Locate(src, "System.Text.StringBuilder.Append");
        var path = Assert.Single(hints, h => h.Span.Line == line);

        Assert.Equal(["StringBuilder", "System.Text"], path.Data);
        Assert.Equal(col, path.Span.Column);
        // Deleting the span leaves `StringBuilder.Append` — the member half is untouched.
        Assert.Equal("System.Text.".Length, path.Span.Length);
    }

    [Fact]
    public void ImportClrStaticMemberPath_IsReported()
    {
        // A static path separates type from member with '/', not '.'.
        var src = """
            (module test)
            (import-clr System [conv System.Convert/ToString : (Int -> String)])
            """;
        var hint = Assert.Single(Hints(src));

        Assert.Equal(["Convert", "System"], hint.Data);
        Assert.Equal("System.".Length, hint.Span.Length);
    }

    [Fact]
    public void ImportClrGenericMemberPath_IsReported()
    {
        // `ICollection` exists only as ICollection`1, so this resolves at no arity the path
        // itself supplies — it pins that the analyzer probes arities rather than asking for
        // Canonical(_, 0), which would report the signature's occurrences but not this one.
        var src = """
            (module test)
            (import-clr
              System.Collections.Generic
              [coll-add System.Collections.Generic.ICollection.Add
                :instance : ((System.Collections.Generic.ICollection String) String -> Unit)])
            """;
        var hints = Hints(src);
        var (line, _) = LspTestSession.Locate(src, "System.Collections.Generic.ICollection.Add");
        var path = Assert.Single(hints, h => h.Span.Line == line);

        Assert.Equal(["ICollection", "System.Collections.Generic"], path.Data);
    }

    [Fact]
    public void PrimitiveClrSpelling_IsReported()
    {
        // `System.String` and `String` both parse to the same ZPrimitiveType, so dropping the
        // qualifier is a pure rename. This used to be excluded outright, back when the two
        // spellings were different types.
        var src = """
            (module test)
            (import-clr System)
            (define (len [s : System.String]) : Int 0)
            """;
        var hint = Assert.Single(Hints(src));

        Assert.Equal(["String", "System"], hint.Data);
    }

    [Fact]
    public void PrimitiveShortNameFromAnotherNamespace_IsNotReported()
    {
        // The carve-out that survives. A primitive keyword is mapped straight to its primitive
        // without consulting the namespace hints, so `Char` here would mean the primitive, not
        // this namespace's type of the same simple name — a change of meaning, not a rename.
        var src = """
            (module test)
            (import-clr Some.Other.Ns)
            (define (f [c : Some.Other.Ns.Char]) : Int 0)
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void ImportClrMemberPathWithAPrimitiveShortName_IsReported()
    {
        // The mirror image of the test above, and the reason the two loops differ: a member
        // path's type half never becomes a ZType, so `String` here is the CLR System.String
        // that ClrInterop reflects on — the primitive carve-out does not apply.
        var src = """
            (module test)
            (import-clr System [str-len System.String.Length :instance-property : (String -> Int)])
            """;
        var hint = Assert.Single(Hints(src));

        Assert.Equal(["String", "System"], hint.Data);
    }

    [Fact]
    public void ImportClrMemberPathWithoutAQualifier_IsNotReported()
    {
        var src = """
            (module test)
            (import-clr System [conv Convert/ToString : (Int -> String)])
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void ImportClrMemberPathWhoseNamespaceIsNotImported_IsNotReported()
    {
        var src = """
            (module test)
            (import-clr
              System.Text
              [dispose System.IDisposable.Dispose :instance : (System.IDisposable -> Unit)])
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void ImportClrMemberPathShadowedByAZSchemeType_IsNotReported()
    {
        var src = """
            (module test)
            (import-clr
              System.Text
              [sb-append System.Text.StringBuilder.Append
                :instance : (StringBuilder String -> StringBuilder)])
            (define-record StringBuilder [n : Int])
            """;

        Assert.Empty(Hints(src));
    }

    [Fact]
    public void ImportClrMemberPathOnTask_IsNotReported()
    {
        // `Task` is in NeverCanonicalized, so the short spelling resolves to nothing and the
        // equality test declines — the same answer the compiler would give.
        var src = """
            (module test)
            (import-clr
              System.Threading.Tasks
              [awaiter System.Threading.Tasks.Task.GetAwaiter
                :instance : ((System.Threading.Tasks.Task Int) -> Object)])
            """;
        var (line, _) = LspTestSession.Locate(src, "System.Threading.Tasks.Task.GetAwaiter");

        Assert.DoesNotContain(Hints(src), h => h.Span.Line == line);
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

    [Fact]
    public void ImportClrMemberPathWithAnInheritedNamespace_IsNotReported()
    {
        // The member-path loop reads the same own-namespaces-only set as the type-position one.
        using var ws = new TempPackageWorkspace(
            "qualmember",
            new Dictionary<string, string>
            {
                ["a.zs"] = """
                (module qualmember/a)
                (import-clr System.Text)
                (export a-marker)
                (define (a-marker) 1)
                """,
                ["b.zs"] = """
                (module qualmember/b)
                (import qualmember/a)
                (import-clr
                  [sb-append System.Text.StringBuilder.Append
                    :instance : (StringBuilder String -> StringBuilder)])
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
