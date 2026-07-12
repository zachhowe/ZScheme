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
    public void Rename_FromRecordDeclarationName_RewritesDeclarationAndUses()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (origin) : Point (Point 0 0))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        // Cursor on the declaration name itself — previously not renameable.
        var (line, col) = LspTestSession.Locate(src, "Point", 1);

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
        // Declaration + constructor call. Type-annotation positions (": Point") are not
        // rewritten — type spans don't survive into ZType (known limitation).
        Assert.Equal(2, edits.Count);
        // The declaration edit covers exactly the name, not the form head.
        var (declLine, declCol) = LspTestSession.Locate(src, "Point", 1);
        var declEdit = Assert.Single(
            edits,
            e => e.Range.Start.Line == declLine - 1 && e.Range.Start.Character == declCol - 1
        );
        Assert.Equal("Point".Length, declEdit.Range.End.Character - declEdit.Range.Start.Character);
    }

    [Fact]
    public void Rename_FromUnionDeclarationAndCaseNames_Works()
    {
        var src = """
            (module test)
            (define-union Shape (Circle [r : Int]) (Square [s : Int]))
            (define (make) : Shape (Circle 1))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;

        // From the union declaration name.
        var (uLine, uCol) = LspTestSession.Locate(src, "Shape", 1);
        var unionEdit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            uLine,
            uCol,
            "Form",
            DocumentUri.Parse(uri)
        );
        Assert.NotNull(unionEdit);
        // Just the declaration — the ": Shape" annotation is a type position (see above).
        Assert.Single(Assert.Single(unionEdit!.Changes!).Value);

        // From a case declaration name.
        var (cLine, cCol) = LspTestSession.Locate(src, "Circle", 1);
        var caseEdit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            cLine,
            cCol,
            "Round",
            DocumentUri.Parse(uri)
        );
        Assert.NotNull(caseEdit);
        Assert.Equal(2, Assert.Single(caseEdit!.Changes!).Value.Count()); // case + constructor call
    }

    [Fact]
    public void Rename_FromClassAndInterfaceDeclarationNames_Works()
    {
        var src = """
            (module test)
            (define-interface IGreet (Hello [] : String))
            (define-class Greeter : IGreet
              (define (Hello) : String "hi"))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;

        var (iLine, iCol) = LspTestSession.Locate(src, "IGreet", 1);
        var ifaceEdit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            iLine,
            iCol,
            "ISpeak",
            DocumentUri.Parse(uri)
        );
        Assert.NotNull(ifaceEdit);
        // Declaration + the class's base-list mention.
        Assert.True(Assert.Single(ifaceEdit!.Changes!).Value.Count() >= 1);

        var (cLine, cCol) = LspTestSession.Locate(src, "Greeter", 1);
        var classEdit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            cLine,
            cCol,
            "Speaker",
            DocumentUri.Parse(uri)
        );
        Assert.NotNull(classEdit);
        var classEdits = Assert.Single(classEdit!.Changes!).Value.ToList();
        var declEdit = Assert.Single(classEdits, e => e.Range.Start.Line == cLine - 1);
        Assert.Equal(cCol - 1, declEdit.Range.Start.Character);
    }

    [Fact]
    public void Rename_ShadowedLocal_OnlyTouchesItsOwnScope()
    {
        var src = """
            (module test)
            (define (f [xx : Int]) : Int
              (let ([xx (* xx 2)])
                (+ xx 1)))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        // Rename initiated from the let's binding site — previously not renameable at all.
        var (line, col) = LspTestSession.Locate(src, "xx", 2);

        var edit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            line,
            col,
            "yy",
            DocumentUri.Parse(uri)
        );

        Assert.NotNull(edit);
        var edits = Assert.Single(edit!.Changes!).Value.ToList();
        // The let binding + its body use. The parameter and the use in the let's
        // value (outer scope) are untouched.
        Assert.Equal(2, edits.Count);
    }

    [Fact]
    public void Rename_OuterParam_SkipsShadowedInnerScope()
    {
        var src = """
            (module test)
            (define (f [xx : Int]) : Int
              (let ([xx (* xx 2)])
                (+ xx 1)))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "xx", 1); // the parameter

        var edit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            line,
            col,
            "yy",
            DocumentUri.Parse(uri)
        );

        Assert.NotNull(edit);
        var edits = Assert.Single(edit!.Changes!).Value.ToList();
        // Param binding + the use in the let's value; the let body's xx is shadowed.
        Assert.Equal(2, edits.Count);
    }

    [Fact]
    public void Rename_PatternVariable_ConfinedToItsArm()
    {
        var src = """
            (module test)
            (define (g [o : (Option Int)]) : Int
              (match o
                [(Some vv) (+ vv 1)]
                [None 0]))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "vv", 1); // the pattern variable

        var edit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            line,
            col,
            "value",
            DocumentUri.Parse(uri)
        );

        Assert.NotNull(edit);
        var edits = Assert.Single(edit!.Changes!).Value.ToList();
        Assert.Equal(2, edits.Count); // pattern binding + body use
    }

    [Fact]
    public void Rename_SameParamNameInTwoFunctions_DoesNotCrossRename()
    {
        var src = """
            (module test)
            (define (f1 [pp : Int]) : Int (+ pp 1))
            (define (f2 [pp : Int]) : Int (* pp 2))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "pp", 1); // f1's parameter

        var edit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            line,
            col,
            "qq",
            DocumentUri.Parse(uri)
        );

        Assert.NotNull(edit);
        var edits = Assert.Single(edit!.Changes!).Value.ToList();
        Assert.Equal(2, edits.Count); // f1's binding + use only
        var f2Line = LspTestSession.Locate(src, "f2").Line - 1;
        Assert.DoesNotContain(edits, e => e.Range.Start.Line == f2Line);
    }

    [Fact]
    public void Rename_TopLevelValue_SkipsShadowingLocalUses()
    {
        var src = """
            (module test)
            (define top-v 1)
            (define (h [n : Int]) : Int
              (let ([top-v (* n 2)])
                (+ top-v n)))
            (define (k) : Int top-v)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "top-v", 1); // the top-level define

        var edit = RenameHandler.ResolveRename(
            state,
            svc.Index,
            line,
            col,
            "top-w",
            DocumentUri.Parse(uri)
        );

        Assert.NotNull(edit);
        var edits = Assert.Single(edit!.Changes!).Value.ToList();
        // Definition + the use in k. The let-bound top-v and its use belong to the local.
        Assert.Equal(2, edits.Count);
    }

    [Fact]
    public void PrepareRename_OnLetBindingName_ReturnsRange()
    {
        var src = """
            (module test)
            (define (f) : Int
              (let ([local-v 41])
                (+ local-v 1)))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "local-v", 1); // the binding site

        var range = PrepareRenameHandler.ResolvePrepareRename(state, line, col);

        Assert.NotNull(range);
        Assert.Equal(line - 1, range!.Start.Line);
        Assert.Equal(col - 1, range.Start.Character);
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
