using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class DocumentHighlightTests
{
    private const string Lib = """
        (module lib)
        (define (lib-double [n : Int]) : Int (* n 2))
        (export lib-double)
        """;

    private const string App = """
        (module app)
        (import xpkg/lib)
        (define (run [n : Int]) : Int (lib-double (lib-double n)))
        """;

    private static string FilePath(string uri) =>
        OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri.Parse(uri).GetFileSystemPath();

    [Fact]
    public void Highlight_Function_MarksDefinitionAndUses()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            (define (area [r : Int]) : Int (square (square r)))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "square");

        var highlights = DocumentHighlightHandler.ResolveHighlights(
            state,
            svc.Index,
            line,
            col,
            FilePath(uri)
        );

        // Definition + two call sites in 'area'.
        Assert.Equal(3, highlights.Count);
        Assert.All(highlights, h => Assert.Equal(DocumentHighlightKind.Text, h.Kind));
    }

    [Fact]
    public void Highlight_Parameter_MarksBindingAndBodyUses()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "x", 2); // parameter binding

        var highlights = DocumentHighlightHandler.ResolveHighlights(
            state,
            svc.Index,
            line,
            col,
            FilePath(uri)
        );

        // Binding + two uses.
        Assert.Equal(3, highlights.Count);
    }

    [Fact]
    public void Highlight_NotOnName_ReturnsEmpty()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;

        // Column 1 of line 2 is the opening paren.
        var highlights = DocumentHighlightHandler.ResolveHighlights(state, svc.Index, 2, 1, FilePath(uri));

        Assert.Empty(highlights);
    }

    [Fact]
    public void Highlight_ShadowedLocal_OnlyMarksItsOwnScope()
    {
        var src = """
            (module test)
            (define (f [xx : Int]) : Int
              (let ([xx (* xx 2)])
                (+ xx 1)))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        // Cursor on the let's binding site (occurrence 2 of "xx").
        var (line, col) = LspTestSession.Locate(src, "xx", 2);

        var highlights = DocumentHighlightHandler.ResolveHighlights(
            state,
            svc.Index,
            line,
            col,
            FilePath(uri)
        );

        // Binding + body use; the parameter and the use in the let's value are a
        // different binding.
        Assert.Equal(2, highlights.Count);
    }

    [Fact]
    public void Highlight_ImportedFunction_ExcludesOtherFiles()
    {
        using var ws = new TempPackageWorkspace(
            "xpkg",
            new Dictionary<string, string> { ["lib.zs"] = Lib, ["app.zs"] = App }
        );
        ws.Open("lib.zs");
        var appState = ws.Open("app.zs");
        var (line, col) = ws.Locate("app.zs", "lib-double"); // a use in app.zs

        var highlights = DocumentHighlightHandler.ResolveHighlights(
            appState,
            ws.Service.Index,
            line,
            col,
            ws.PathOf("app.zs")
        );

        // Two uses in app.zs; the declaration in lib.zs is excluded (single-file).
        Assert.Equal(2, highlights.Count);
    }
}
