using System.Runtime.CompilerServices;
using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class ImportClrTests
{
    private static (AnalysisService Service, string Uri) NewSession(
        string source,
        [CallerMemberName] string testName = ""
    )
    {
        return LspTestSession.Open(source, testName: testName);
    }

    [Fact]
    public void ImportClr_FromZunit_ResolvesXunitAssert()
    {
        var src = """
            (module test)
            (import zunit)
            (define (t) (check-equal? 1 1))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        Assert.DoesNotContain(
            state.Diagnostics.Diagnostics,
            d => d.Message.Contains("CLR type not found")
        );
    }

    [Fact]
    public void ImportClr_DirectXunitImport_ResolvesViaPackageNuGetDeps()
    {
        var src = """
            (module test)
            (import-clr [equal Xunit.Assert/Equal ^a])
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        Assert.DoesNotContain(
            state.Diagnostics.Diagnostics,
            d => d.Message.Contains("CLR type not found")
        );
    }

    /// <summary>
    ///     The editor is where a short-vs-fully-qualified mismatch showed up first: the language
    ///     server runs the real front-end but stops after type inference, so it never saw the IL
    ///     emitter's namespace-prefix rescue and reported a bare `Type mismatch` instead. It also
    ///     needs the file's *own* `(import-clr Ns ...)` hints, which used to be collected a stage
    ///     too late to reach that file's annotations.
    /// </summary>
    [Fact]
    public void ImportClr_ShortAndFullyQualifiedNames_AreInterchangeable()
    {
        var src = """
            (module test)
            (import-clr
              System.Text
              [sb-append System.Text.StringBuilder.Append
                :instance : (System.Text.StringBuilder String -> System.Text.StringBuilder)])

            (define (grow [b : StringBuilder]) : System.Text.StringBuilder
              (sb-append b "x"))

            (define (grow2 [b : System.Text.StringBuilder]) : StringBuilder
              (grow b))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        Assert.DoesNotContain(
            state.Diagnostics.Diagnostics,
            d => d.Message.Contains("Type mismatch") || d.Message.Contains("CLR type not found")
        );
    }

    [Fact]
    public void ImportClr_UnknownType_StillProducesNotFoundDiagnostic()
    {
        // No package declares this nonsense type, so the LSP should surface the
        // standard "CLR type not found" diagnostic even though NuGet resolution runs.
        var src = """
            (module test)
            (import-clr [whatever Definitely.Does.Not.Exist/SomeMethod ^a])
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        Assert.Contains(
            state.Diagnostics.Diagnostics,
            d => d.Message.Contains("CLR type not found")
        );
    }
}
