using System.Runtime.CompilerServices;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class HoverTests
{
    private static (Analysis.AnalysisService Service, string Uri) NewSession(
        string source, [CallerMemberName] string testName = "")
    {
        return LspTestSession.Open(source, testName: testName);
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

    [Fact]
    public void Hover_OnUnionDeclName_ReturnsUnionHeader()
    {
        var src = """
            (module test)
            (union Shape (Circle [r : Int]) (Square [s : Int]))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // "Shape" name is at column 8 on line 2.
        var hover = HoverHandler.ResolveHover(state, line: 2, col: 9);

        Assert.NotNull(hover);
        Assert.Contains("union", hover.Value.Markdown);
        Assert.Contains("Shape", hover.Value.Markdown);
    }

    [Fact]
    public void Hover_OnClassDeclName_ReturnsClassHeader()
    {
        var src = """
            (module test)
            (class MyBox
              [value : Int])
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // "MyBox" starts at column 8 on line 2.
        var hover = HoverHandler.ResolveHover(state, line: 2, col: 9);

        Assert.NotNull(hover);
        Assert.Contains("class", hover.Value.Markdown);
        Assert.Contains("MyBox", hover.Value.Markdown);
    }

    [Fact]
    public void Hover_OnInterfaceDeclName_ReturnsInterfaceHeader()
    {
        var src = """
            (module test)
            (interface IBox
              (Get [] : Int))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // "IBox" starts at column 12 on line 2.
        var hover = HoverHandler.ResolveHover(state, line: 2, col: 13);

        Assert.NotNull(hover);
        Assert.Contains("interface", hover.Value.Markdown);
        Assert.Contains("IBox", hover.Value.Markdown);
    }
}
