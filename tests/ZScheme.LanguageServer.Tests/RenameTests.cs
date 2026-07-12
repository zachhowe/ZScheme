using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class RenameTests
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

    [Fact]
    public void Rename_TopLevelFunction_RewritesDefinitionAndCallSites()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            (define (area [r : Int]) : Int (square r))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "square"); // the definition name

        var edit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            line,
            col,
            "sq",
            DocumentUri.Parse(uri)
        );

        Assert.NotNull(edit);
        var edits = Assert.Single(edit!.Changes!).Value.ToList();
        // Definition site + call site in 'area'.
        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.Equal("sq", e.NewText));
        // The replaced range spans the old name's length (6 = "square"), regardless of new name.
        Assert.All(edits, e => Assert.Equal(6, e.Range.End.Character - e.Range.Start.Character));
    }

    [Fact]
    public void Rename_RecordDeclaration_IncludesDeclarationSpan()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (origin) : Point (Point 0 0))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        // Resolve from the (Point 0 0) constructor usage — the declaration name itself is
        // not a Name node, but its span is still included in the edit.
        var (line, col) = LspTestSession.Locate(src, "Point", 3);

        var edit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            line,
            col,
            "Coord",
            DocumentUri.Parse(uri)
        );

        Assert.NotNull(edit);
        var edits = Assert.Single(edit!.Changes!).Value.ToList();
        // The declaration itself must be renamed even though it has no Name occurrence.
        var declLine = LspTestSession.Locate(src, "Point").Line - 1;
        Assert.Contains(edits, e => e.Range.Start.Line == declLine);
        Assert.All(edits, e => Assert.Equal("Coord", e.NewText));
    }

    [Fact]
    public void Rename_Parameter_ConfinedToEnclosingFunction()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "x", 2); // the parameter binding

        var edit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            line,
            col,
            "n",
            DocumentUri.Parse(uri)
        );

        Assert.NotNull(edit);
        var edits = Assert.Single(edit!.Changes!).Value.ToList();
        // Binding + two uses in the body.
        Assert.Equal(3, edits.Count);
        Assert.All(edits, e => Assert.Equal("n", e.NewText));
    }

    [Fact]
    public void PrepareRename_OnIdentifier_ReturnsRange()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "square");

        var range = PrepareRenameHandler.ResolvePrepareRename(state, line, col + 1);

        Assert.NotNull(range);
        Assert.Equal(line - 1, range!.Start.Line);
    }

    [Fact]
    public void PrepareRename_OnNonIdentifier_ReturnsNull()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        // Column 1 of line 2 is the opening paren of the define form.
        var range = PrepareRenameHandler.ResolvePrepareRename(state, 2, 1);

        Assert.Null(range);
    }

    [Fact]
    public void Rename_ImportedFunction_SpansMultipleFiles()
    {
        using var ws = new TempPackageWorkspace(
            "xpkg",
            new Dictionary<string, string> { ["lib.zs"] = Lib, ["app.zs"] = App }
        );
        ws.Open("lib.zs");
        ws.Open("app.zs");

        var libState = ws.Service.GetDocument(ws.UriOf("lib.zs"))!;
        var (line, col) = ws.Locate("lib.zs", "lib-double"); // declaration

        var edit = RenameHandler.ResolveRename(
            libState,
            ws.Service.Index,
            line,
            col,
            "twice",
            DocumentUri.FromFileSystemPath(ws.PathOf("lib.zs"))
        );

        Assert.NotNull(edit);
        var files = edit!.Changes!.Keys.Select(u => u.GetFileSystemPath()).ToHashSet();
        Assert.Contains(ws.PathOf("lib.zs"), files);
        Assert.Contains(ws.PathOf("app.zs"), files);
    }
}
