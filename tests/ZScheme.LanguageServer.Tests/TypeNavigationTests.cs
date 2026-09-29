using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.Compiler.Analysis;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;
using SymbolKind = ZScheme.LanguageServer.Analysis.SymbolKind;

namespace ZScheme.LanguageServer.Tests;

/// <summary>
///     Navigation on type names written in type positions — parameter/return
///     annotations, nested type applications, class/interface base lists. These sites
///     have no <c>Name</c> node, so they resolve through <see cref="TypeNavigation" />
///     over the compiler's recorded <c>Program.TypeNameUses</c>.
/// </summary>
public sealed class TypeNavigationTests
{
    // --- definition on type-annotation sites ---

    [Fact]
    public void Definition_OnParameterAnnotation_ResolvesToRecordDecl()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (f [p : Point]) : Point p)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "Point", 2); // [p : Point]

        var span = DefinitionHandler.ResolveDefinition(state, line, col, svc.Index);

        // The declaration name — not the whole record form, not a constructor call.
        var (dLine, dCol) = LspTestSession.Locate(src, "Point");
        Assert.NotNull(span);
        Assert.Equal(dLine, span.Value.Line);
        Assert.Equal(dCol, span.Value.Column);
        Assert.Equal("Point".Length, span.Value.Length);
    }

    [Fact]
    public void Definition_OnReturnAnnotation_ResolvesToUnionDecl()
    {
        var src = """
            (module test)
            (define-union Shape (Circle [r : Int]) (Square [s : Int]))
            (define (mk) : Shape (Circle 1))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "Shape", 2); // the : Shape return type

        var span = DefinitionHandler.ResolveDefinition(state, line, col, svc.Index);

        var (dLine, dCol) = LspTestSession.Locate(src, "Shape");
        Assert.NotNull(span);
        Assert.Equal(dLine, span.Value.Line);
        Assert.Equal(dCol, span.Value.Column);
        Assert.Equal("Shape".Length, span.Value.Length);
    }

    [Fact]
    public void Definition_OnNestedTypeApplication_ResolvesBothNames()
    {
        var src = """
            (module test)
            (define-record Point [x : Int])
            (define-union (Maybe ^a)
              (Just [value : ^a])
              (Nothing))
            (define (f [m : (Maybe Point)]) : (Maybe Point) m)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;

        // The outer name of the application.
        var (mLine, mCol) = LspTestSession.Locate(src, "Maybe", 2); // [m : (Maybe Point)]
        var maybeSpan = DefinitionHandler.ResolveDefinition(state, mLine, mCol, svc.Index);
        var (dMaybeLine, dMaybeCol) = LspTestSession.Locate(src, "Maybe");
        Assert.NotNull(maybeSpan);
        Assert.Equal(dMaybeLine, maybeSpan.Value.Line);
        Assert.Equal(dMaybeCol, maybeSpan.Value.Column);

        // The type argument resolves independently of the outer name.
        var (pLine, pCol) = LspTestSession.Locate(src, "Point", 2); // Point inside (Maybe Point)
        var pointSpan = DefinitionHandler.ResolveDefinition(state, pLine, pCol, svc.Index);
        var (dPointLine, dPointCol) = LspTestSession.Locate(src, "Point");
        Assert.NotNull(pointSpan);
        Assert.Equal(dPointLine, pointSpan.Value.Line);
        Assert.Equal(dPointCol, pointSpan.Value.Column);
    }

    [Fact]
    public void Definition_OnClassBaseList_ResolvesBaseClassAndInterface()
    {
        var src = """
            (module test)
            (define-interface IFoo (Foo [] : Int))
            (define-class #:open Base [value : Int])
            (define-class Dog : Base IFoo
              (define (Foo) : Int 1))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;

        var (bLine, bCol) = LspTestSession.Locate(src, "Base", 2); // the base list
        var baseSpan = DefinitionHandler.ResolveDefinition(state, bLine, bCol, svc.Index);
        var (dBLine, dBCol) = LspTestSession.Locate(src, "Base");
        Assert.NotNull(baseSpan);
        Assert.Equal(dBLine, baseSpan.Value.Line);
        Assert.Equal(dBCol, baseSpan.Value.Column);

        var (iLine, iCol) = LspTestSession.Locate(src, "IFoo", 2); // the interface in the base list
        var ifaceSpan = DefinitionHandler.ResolveDefinition(state, iLine, iCol, svc.Index);
        var (dILine, dICol) = LspTestSession.Locate(src, "IFoo");
        Assert.NotNull(ifaceSpan);
        Assert.Equal(dILine, ifaceSpan.Value.Line);
        Assert.Equal(dICol, ifaceSpan.Value.Column);
    }

    [Fact]
    public void Definition_OnPrimitiveTypeUse_ReturnsNull()
    {
        var src = """
            (module test)
            (define (f [x : Int]) : Int x)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, ": Int", 1);

        // Primitives have no declaration to jump to — the navigation declines.
        Assert.Null(DefinitionHandler.ResolveDefinition(state, line, col, svc.Index));
    }

    [Fact]
    public void Definition_AmbiguousTypeName_DeclinesRatherThanGuesses()
    {
        var src = """
            (module test)
            (define (f [w : Widget]) : Widget w)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        Assert.NotNull(state.Ast); // the undefined type must not void the recorded uses
        var (line, col) = LspTestSession.Locate(src, "Widget");

        // Two files declare a Widget record. Type uses carry no qualified key, so a
        // bare-name match cannot tell which declaration the annotation means — decline.
        var index = new WorkspaceIndex();
        index.UpdateFile(
            "/a/one.zs",
            [
                new IndexedDefinition(
                    "pkg1/Widget",
                    "Widget",
                    new SourceSpan("/a/one.zs", 2, 20, 6),
                    SymbolKind.Record,
                    "pkg1"
                ),
            ],
            []
        );
        index.UpdateFile(
            "/a/two.zs",
            [
                new IndexedDefinition(
                    "pkg2/Widget",
                    "Widget",
                    new SourceSpan("/a/two.zs", 2, 20, 6),
                    SymbolKind.Record,
                    "pkg2"
                ),
            ],
            []
        );

        Assert.Null(DefinitionHandler.ResolveDefinition(state, line, col, index));
    }

    [Fact]
    public void Definition_SameFileTypeWinsOverSameNamedFunction()
    {
        // A value named Widget and a type named Widget: the type position can only mean
        // the type.
        var src = """
            (module test)
            (define Widget 1)
            (define-record Widget [x : Int])
            (define (f [w : Widget]) : Int 0)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "Widget", 3); // [w : Widget]

        var span = DefinitionHandler.ResolveDefinition(state, line, col, svc.Index);

        var (dLine, dCol) = LspTestSession.Locate(src, "Widget", 2); // the record decl
        Assert.NotNull(span);
        Assert.Equal(dLine, span.Value.Line);
        Assert.Equal(dCol, span.Value.Column);
    }

    // --- type definition (F12 on a type annotation) ---

    [Fact]
    public void TypeDefinition_OnAnnotation_ResolvesToDeclaredType()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (f [p : Point]) : Point p)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "Point", 2); // [p : Point]

        var spans = TypeDefinitionHandler.Resolve(state, svc.Index, line, col);

        var (dLine, _) = LspTestSession.Locate(src, "Point");
        var span = Assert.Single(spans);
        Assert.Equal(dLine, span.Line);
    }

    // --- rename ---

    [Fact]
    public void Rename_FromAnnotationSite_RewritesDeclarationAnnotationsAndCallSites()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (f [p : Point]) : Point (Point 1 2))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "Point", 2); // [p : Point]

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
        // Declaration + both annotations + the constructor call.
        Assert.Equal(4, edits.Count);
        Assert.All(edits, e => Assert.Equal("Coord", e.NewText));
        // Every edit spans exactly the old name's length.
        Assert.All(
            edits,
            e => Assert.Equal("Point".Length, e.Range.End.Character - e.Range.Start.Character)
        );
        // The declaration itself is rewritten, and each annotation site is.
        var (dLine, _) = LspTestSession.Locate(src, "Point");
        var (a1Line, _) = LspTestSession.Locate(src, "Point", 2);
        var (a2Line, _) = LspTestSession.Locate(src, "Point", 3);
        var (cLine, _) = LspTestSession.Locate(src, "Point", 4);
        var lines = edits.Select(e => e.Range.Start.Line).ToHashSet();
        Assert.Contains(dLine - 1, lines);
        Assert.Contains(a1Line - 1, lines);
        Assert.Contains(a2Line - 1, lines);
        Assert.Contains(cLine - 1, lines);
    }

    // --- document highlight ---

    [Fact]
    public void Highlight_OnTypeAnnotation_CoversDeclarationAndAnnotations()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (f [p : Point]) : Point p)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var filePath = DocumentUri.Parse(uri).GetFileSystemPath();
        var (line, col) = LspTestSession.Locate(src, "Point", 2); // [p : Point]

        var highlights = DocumentHighlightHandler.ResolveHighlights(
            state,
            svc.Index,
            line,
            col,
            filePath
        );

        // Declaration + both annotation sites.
        Assert.Equal(3, highlights.Count);
    }

    // --- references ---

    [Fact]
    public void References_OnTypeAnnotation_ExcludesUnrelatedSameNamedLocal()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (f [p : Point]) : Point p)
            (define (g) : Int
              (let ([Point 1])
                (+ Point 1)))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "Point", 1); // the declaration

        var refs = ReferencesHandler.ResolveReferences(
            state,
            svc.Index,
            line,
            col,
            includeDeclaration: true,
            DocumentUri.Parse(uri)
        );

        // Declaration + both annotations in f. The let-bound Point and its use in g
        // belong to a different symbol and must not appear.
        var (dLine, _) = LspTestSession.Locate(src, "Point", 1);
        var (a1Line, _) = LspTestSession.Locate(src, "Point", 2);
        var (a2Line, _) = LspTestSession.Locate(src, "Point", 3);
        var (localLine, _) = LspTestSession.Locate(src, "Point", 4);
        var (useLine, _) = LspTestSession.Locate(src, "Point", 5);
        var lines = refs.Select(r => r.Range.Start.Line).ToHashSet();
        Assert.Equal(3, refs.Count);
        Assert.Contains(dLine - 1, lines);
        Assert.Contains(a1Line - 1, lines);
        Assert.Contains(a2Line - 1, lines);
        Assert.DoesNotContain(localLine - 1, lines);
        Assert.DoesNotContain(useLine - 1, lines);
    }

    // --- prepare rename / hover ---

    [Fact]
    public void PrepareRename_OnTypeAnnotation_ReturnsNameRange()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (f [p : Point]) : Point p)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "Point", 2); // [p : Point]

        var range = PrepareRenameHandler.ResolvePrepareRename(state, line, col);

        Assert.NotNull(range);
        Assert.Equal(line - 1, range!.Start.Line);
        Assert.Equal(col - 1, range.Start.Character);
        Assert.Equal("Point".Length, range.End.Character - range.Start.Character);
    }

    [Fact]
    public void PrepareRename_OnNullableAnnotation_RangeExcludesTheNullableSuffix()
    {
        // The range is the identifier to rename: exactly the name, not `Point?` — a wider
        // range would make the rename delete the nullable suffix.
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (f [p : Point?]) : Int 0)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "Point", 2); // [p : Point?]

        var range = PrepareRenameHandler.ResolvePrepareRename(state, line, col);

        Assert.NotNull(range);
        Assert.Equal(line - 1, range!.Start.Line);
        Assert.Equal(col - 1, range.Start.Character);
        Assert.Equal("Point".Length, range.End.Character - range.Start.Character);
    }

    [Fact]
    public void Rename_FromNullableAnnotation_RewritesNameButKeepsTheSuffix()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (f [p : Point?]) : Int 0)
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "Point", 2); // [p : Point?]

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
        // The declaration plus the annotation use.
        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.Equal("Coord", e.NewText));
        // The annotation edit spans exactly the old name's length (not "Point?".Length),
        // so applying it rewrites `[p : Point?]` to `[p : Coord?]` — the `?` survives.
        var useEdit = Assert.Single(edits, e => e.Range.Start.Line == line - 1);
        Assert.Equal(col - 1, useEdit.Range.Start.Character);
        Assert.Equal("Point".Length, useEdit.Range.End.Character - useEdit.Range.Start.Character);
    }

    // --- cross-file ---

    private const string Lib = """
        (module lib)
        (define-record Point [x : Int] [y : Int])
        (export Point)
        """;

    private const string App = """
        (module app)
        (import xpkg/lib)
        (define (describe [p : Point]) : Point p)
        """;

    [Fact]
    public void Definition_OnCrossFileTypeAnnotation_JumpsToDefiningFile()
    {
        using var ws = new TempPackageWorkspace(
            "xpkg",
            new Dictionary<string, string> { ["lib.zs"] = Lib, ["app.zs"] = App }
        );
        ws.Open("lib.zs");
        var appState = ws.Open("app.zs");

        var (line, col) = ws.Locate("app.zs", "Point", 1); // [p : Point]
        var span = DefinitionHandler.ResolveDefinition(appState, line, col, ws.Service.Index);

        Assert.NotNull(span);
        Assert.Equal(ws.PathOf("lib.zs"), span.Value.File);
        Assert.Equal(2, span.Value.Line); // (define-record Point ...
    }

    [Fact]
    public void TypeDefinition_OnCrossFileTypeAnnotation_JumpsToDefiningFile()
    {
        using var ws = new TempPackageWorkspace(
            "xpkg",
            new Dictionary<string, string> { ["lib.zs"] = Lib, ["app.zs"] = App }
        );
        ws.Open("lib.zs");
        var appState = ws.Open("app.zs");

        var (line, col) = ws.Locate("app.zs", "Point", 2); // the : Point return type
        var spans = TypeDefinitionHandler.Resolve(appState, ws.Service.Index, line, col);

        var span = Assert.Single(spans);
        Assert.Equal(ws.PathOf("lib.zs"), span.File);
        Assert.Equal(2, span.Line);
    }

    [Fact]
    public void Rename_CrossFileType_RewritesDeclarationAndAnnotationsAcrossFiles()
    {
        using var ws = new TempPackageWorkspace(
            "xpkg",
            new Dictionary<string, string> { ["lib.zs"] = Lib, ["app.zs"] = App }
        );
        ws.Open("lib.zs");
        ws.Open("app.zs");

        // Initiated from the declaration in lib.zs ...
        var libState = ws.Service.GetDocument(ws.UriOf("lib.zs"))!;
        var (dLine, dCol) = ws.Locate("lib.zs", "Point");
        var fromDecl = RenameHandler.ResolveRename(
            libState,
            ws.Service.Index,
            dLine,
            dCol,
            "Coord",
            DocumentUri.FromFileSystemPath(ws.PathOf("lib.zs"))
        );

        Assert.NotNull(fromDecl);
        var declFiles = fromDecl!.Changes!.Keys.Select(u => u.GetFileSystemPath()).ToHashSet();
        Assert.Equal(2, declFiles.Count);
        // lib.zs: the declaration. app.zs: both annotation sites.
        Assert.Single(fromDecl.Changes[DocumentUri.FromFileSystemPath(ws.PathOf("lib.zs"))]);
        Assert.Equal(
            2,
            fromDecl.Changes[DocumentUri.FromFileSystemPath(ws.PathOf("app.zs"))].Count()
        );
        Assert.All(
            fromDecl.Changes.Values.SelectMany(v => v),
            e => Assert.Equal("Coord", e.NewText)
        );

        // ... and from an annotation in app.zs: the same edit set.
        var appState = ws.Service.GetDocument(ws.UriOf("app.zs"))!;
        var (aLine, aCol) = ws.Locate("app.zs", "Point", 1);
        var fromUse = RenameHandler.ResolveRename(
            appState,
            ws.Service.Index,
            aLine,
            aCol,
            "Coord",
            DocumentUri.FromFileSystemPath(ws.PathOf("app.zs"))
        );

        Assert.NotNull(fromUse);
        Assert.Equal(fromDecl.Changes!.Count, fromUse!.Changes!.Count);
        var useEdits = fromUse.Changes.Values.SelectMany(v => v).ToList();
        Assert.Equal(3, useEdits.Count);
        Assert.All(useEdits, e => Assert.Equal("Coord", e.NewText));
    }

    [Fact]
    public void References_OnCrossFileType_IncludesAnnotationUsesInOtherFiles()
    {
        using var ws = new TempPackageWorkspace(
            "xpkg",
            new Dictionary<string, string> { ["lib.zs"] = Lib, ["app.zs"] = App }
        );
        ws.Open("lib.zs");
        ws.Open("app.zs");

        var libState = ws.Service.GetDocument(ws.UriOf("lib.zs"))!;
        var (dLine, dCol) = ws.Locate("lib.zs", "Point"); // the declaration

        var refs = ReferencesHandler.ResolveReferences(
            libState,
            ws.Service.Index,
            dLine,
            dCol,
            includeDeclaration: false,
            DocumentUri.FromFileSystemPath(ws.PathOf("lib.zs"))
        );

        // The two annotation sites in app.zs; no lib.zs occurrences (declaration
        // excluded, and lib.zs has no uses of its own).
        var files = refs.Select(r => r.Uri.GetFileSystemPath()).ToHashSet();
        Assert.Equal(new HashSet<string> { ws.PathOf("app.zs") }, files);
        Assert.Equal(2, refs.Count);
    }

    // --- drift guard: the two type-position grammars must stay in step ---

    /// <summary>
    ///     For a corpus file, every <c>TypeNameUse</c> the compiler records must also be
    ///     reported by <see cref="TypeNameScanner.Scan" /> (same name, span and arity).
    ///     The scanner reads the raw token stream (the ZS0004 analyzer depends on it) and
    ///     <c>TypeNavigation</c> reads the compiler's recorded uses — if either grammar
    ///     drifts, a navigation site stops matching a diagnostic site (or vice versa).
    /// </summary>
    [Theory]
    [InlineData("option.zs")]
    [InlineData("core.zs")]
    public void TypeNameUses_MatchTypeNameScannerOnCorpusFiles(string file)
    {
        var root = LspTestSession.FindRepoRoot();
        var path = Path.Combine(root, "packages", "stdlib", "src", file);
        var source = File.ReadAllText(path);

        var compilation = new Compilation(
            new CompilerOptions { StopAfterTypeInference = true, AllowsImplicitModuleName = true }
        );
        compilation.Compile(source, path);
        Assert.NotNull(compilation.TypedProgram);
        var program = compilation.TypedProgram!;
        Assert.NotEmpty(program.TypeNameUses); // the corpus file must exercise the grammar

        var scanned = TypeNameScanner
            .Scan(LexicalStructure.Tokens(source))
            .TypeNames.Select(t =>
                (t.Name, t.Token.Span.Line, t.Token.Span.Column, t.Token.Span.Length, t.Arity)
            )
            .ToHashSet();

        var missing = program
            .TypeNameUses.Select(u => (u.Name, u.Span.Line, u.Span.Column, u.Span.Length, u.Arity))
            .Where(t => !scanned.Contains(t))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{file}: {missing.Count} TypeNameUse(s) missing from TypeNameScanner output: "
                + string.Join(
                    ", ",
                    missing.Select(m => $"{m.Item1}@line {m.Item2} col {m.Item3} (arity {m.Item5})")
                )
        );
    }
}
