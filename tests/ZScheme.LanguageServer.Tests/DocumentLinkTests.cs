using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class DocumentLinkTests
{
    private static TempPackageWorkspace NewWorkspace(string appSource)
    {
        return new TempPackageWorkspace(
            "dlpkg",
            new Dictionary<string, string>
            {
                ["lib.zs"] = """
                    (module lib)
                    (define (helper [n : Int]) : Int n)
                    (export helper)
                    """,
                ["other.zs"] = """
                    (module other)
                    (define (assist [n : Int]) : Int n)
                    (export assist)
                    """,
                ["app.zs"] = appSource,
            }
        );
    }

    [Fact]
    public void SingleImport_LinksModuleNameToFile()
    {
        using var ws = NewWorkspace("""
            (module app)
            (import dlpkg/lib)
            (define (run [n : Int]) : Int (helper n))
            """);
        var state = ws.Open("app.zs");

        var links = DocumentLinkHandler.Compute(
            state,
            m => ws.Service.ResolveModulePath(ws.PathOf("app.zs"), m)
        );

        var link = Assert.Single(links);
        Assert.Equal(ws.PathOf("lib.zs"), link.Target!.GetFileSystemPath());

        // Range covers exactly the module name, not the parens or keyword.
        var (line, col) = ws.Locate("app.zs", "dlpkg/lib");
        Assert.Equal(line - 1, link.Range.Start.Line);
        Assert.Equal(col - 1, link.Range.Start.Character);
        Assert.Equal(col - 1 + "dlpkg/lib".Length, link.Range.End.Character);
    }

    [Fact]
    public void MultiImport_ProducesOneLinkPerModule()
    {
        using var ws = NewWorkspace("""
            (module app)
            (import dlpkg/lib dlpkg/other)
            (define (run [n : Int]) : Int (assist (helper n)))
            """);
        var state = ws.Open("app.zs");

        var links = DocumentLinkHandler.Compute(
            state,
            m => ws.Service.ResolveModulePath(ws.PathOf("app.zs"), m)
        );

        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.Target!.GetFileSystemPath() == ws.PathOf("lib.zs"));
        Assert.Contains(links, l => l.Target!.GetFileSystemPath() == ws.PathOf("other.zs"));
    }

    [Fact]
    public void UnresolvableImport_ProducesNoLink()
    {
        using var ws = NewWorkspace("""
            (module app)
            (import nonexistent/nope)
            (define x 1)
            """);
        var state = ws.Open("app.zs");

        var links = DocumentLinkHandler.Compute(
            state,
            m => ws.Service.ResolveModulePath(ws.PathOf("app.zs"), m)
        );

        Assert.Empty(links);
    }
}
