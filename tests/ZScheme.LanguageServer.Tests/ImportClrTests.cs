using System.Runtime.CompilerServices;
using Xunit;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class ImportClrTests
{
    private static (Analysis.AnalysisService Service, string Uri) NewSession(
        string source, [CallerMemberName] string testName = "")
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

        Assert.DoesNotContain(state.Diagnostics.Diagnostics, d => d.Message.Contains("CLR type not found"));
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

        Assert.DoesNotContain(state.Diagnostics.Diagnostics, d => d.Message.Contains("CLR type not found"));
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

        Assert.Contains(state.Diagnostics.Diagnostics, d => d.Message.Contains("CLR type not found"));
    }
}
