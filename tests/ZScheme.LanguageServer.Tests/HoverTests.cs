using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;

namespace ZScheme.LanguageServer.Tests;

public sealed class HoverTests
{
    private static (AnalysisService Service, string Uri) NewSession(string source, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        // Place the synthetic file inside the repo so DiscoverPackagePaths finds packages/.
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
    public void Hover_OnTopLevelDefineName_ReturnsType()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // "square" starts at column 10 on line 2 (1-based).
        var hover = HoverHandler.ResolveHover(state, line: 2, col: 12);

        Assert.NotNull(hover);
        Assert.Contains("square", hover.Value.Markdown);
        Assert.Contains("Int", hover.Value.Markdown);
    }

    [Fact]
    public void Hover_OnParameter_ReturnsParameterType()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // The parameter binding "x" is at column 18 on line 2.
        var hover = HoverHandler.ResolveHover(state, line: 2, col: 18);

        Assert.NotNull(hover);
        Assert.Contains("x", hover.Value.Markdown);
        Assert.Contains("Int", hover.Value.Markdown);
    }

    [Fact]
    public void Hover_OnParameterReferenceInBody_ReturnsType()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // "x" reference inside the body, in (* x x).
        var hover = HoverHandler.ResolveHover(state, line: 2, col: 37);

        Assert.NotNull(hover);
        Assert.Contains("x", hover.Value.Markdown);
        Assert.Contains("Int", hover.Value.Markdown);
    }

    [Fact]
    public void Hover_OnRecordDeclName_ReturnsRecord()
    {
        var src = """
            (module test)
            (record Point [x : Int] [y : Int])
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // "Point" name at column 9 on line 2.
        var hover = HoverHandler.ResolveHover(state, line: 2, col: 10);

        Assert.NotNull(hover);
        Assert.Contains("Point", hover.Value.Markdown);
    }

    [Fact]
    public void Hover_DuringTransientParseError_UsesLastGoodAst()
    {
        var goodSrc = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var brokenSrc = """
            (module test)
            (define (square [x : Int]) : Int (* x x
            """;

        var (svc, uri) = NewSession(goodSrc);
        // Introduce a parse error
        svc.AnalyzeImmediate(uri, brokenSrc, version: 2);

        var state = svc.GetDocument(uri)!;
        Assert.NotNull(state.Ast); // last-good AST preserved

        var hover = HoverHandler.ResolveHover(state, line: 2, col: 12);
        Assert.NotNull(hover);
        Assert.Contains("square", hover.Value.Markdown);
    }

    [Fact]
    public void Hover_OnGenericFunction_RendersDistinctTypeParams()
    {
        var src = """
            (module test)
            (define (apply-fn [f : (Fn [^a] ^b)] [x : ^a]) : ^b (f x))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // Hover on the function name "apply-fn".
        var hover = HoverHandler.ResolveHover(state, line: 2, col: 12);

        Assert.NotNull(hover);
        Assert.Contains("^a", hover.Value.Markdown);
        Assert.Contains("^b", hover.Value.Markdown);
        Assert.DoesNotContain("?", hover.Value.Markdown);
        Assert.DoesNotContain("t0", hover.Value.Markdown);
    }

    [Fact]
    public void Hover_OnMultiLineDefineName_ReturnsType()
    {
        // The outer Define span only covers the first line, so prior to the NameSpan
        // fix, the cursor on "square" never matched any node and hover was empty.
        var src = """
            (module test)
            (define (square [x : Int]) : Int
              (* x x))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        var hover = HoverHandler.ResolveHover(state, line: 2, col: 12);

        Assert.NotNull(hover);
        Assert.Contains("square", hover.Value.Markdown);
        Assert.Contains("Int", hover.Value.Markdown);
    }

    [Fact]
    public void Hover_OnMultiLineDefineValueName_ReturnsType()
    {
        var src = """
            (module test)
            (define answer
              42)
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // "answer" starts at column 9 on line 2.
        var hover = HoverHandler.ResolveHover(state, line: 2, col: 10);

        Assert.NotNull(hover);
        Assert.Contains("answer", hover.Value.Markdown);
    }

    [Fact]
    public void Hover_OnSameTypeVarUsedTwice_RendersSameName()
    {
        var src = """
            (module test)
            (define (pair [x : ^a] [y : ^a]) : ^a x)
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // Hover on "pair".
        var hover = HoverHandler.ResolveHover(state, line: 2, col: 11);

        Assert.NotNull(hover);
        Assert.Contains("^a", hover.Value.Markdown);
        Assert.DoesNotContain("^b", hover.Value.Markdown);
        Assert.DoesNotContain("?", hover.Value.Markdown);
    }
}
