using Xunit;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Tests;

public sealed class ImportClrTests
{
    private static (AnalysisService Service, string Uri) NewSession(
        string source,
        [System.Runtime.CompilerServices.CallerMemberName]
        string testName = "")
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "tests", "ZScheme.LanguageServer.Tests", "tmp", $"{testName}.zs");
        var uri = new Uri(path).AbsoluteUri;

        var service = new AnalysisService();
        service.AnalyzeImmediate(uri, source, version: 1);
        return (service, uri);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "packages")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not locate repo root with packages/ directory");
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
}
