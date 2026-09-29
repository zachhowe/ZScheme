using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Tests;

public sealed class WorkspaceIndexTests
{
    private static IndexedDefinition Def(string module, string name, string file, SymbolKind kind)
    {
        return new IndexedDefinition(
            $"{module}/{name}",
            name,
            new SourceSpan(file, 1, 1, name.Length),
            kind,
            module
        );
    }

    private static IndexedReference Ref(string name, string? key, string file, int line)
    {
        return new IndexedReference(name, key, new SourceSpan(file, line, 1, name.Length));
    }

    [Fact]
    public void ResolveDefinition_PrefersQualifiedKey()
    {
        var index = new WorkspaceIndex();
        index.UpdateFile(
            "/a/lib.zs",
            [Def("pkg/lib", "foo", "/a/lib.zs", SymbolKind.Function)],
            []
        );

        var defs = index.ResolveDefinition("pkg/lib/foo", "foo");

        Assert.Single(defs);
        Assert.Equal("/a/lib.zs", defs[0].File);
    }

    [Fact]
    public void ResolveDefinition_FallsBackToBareName()
    {
        var index = new WorkspaceIndex();
        index.UpdateFile(
            "/a/lib.zs",
            [Def("pkg/lib", "foo", "/a/lib.zs", SymbolKind.Function)],
            []
        );

        // No qualified key: still resolves by bare name.
        var defs = index.ResolveDefinition(null, "foo");

        Assert.Single(defs);
        Assert.Equal("foo", defs[0].BareName);
    }

    [Fact]
    public void FindReferences_MatchesQualifiedKeyAcrossFiles()
    {
        var index = new WorkspaceIndex();
        index.UpdateFile(
            "/a/lib.zs",
            [Def("pkg/lib", "foo", "/a/lib.zs", SymbolKind.Function)],
            [Ref("foo", null, "/a/lib.zs", 1)]
        );
        index.UpdateFile(
            "/a/app.zs",
            [],
            [Ref("foo", "pkg/lib/foo", "/a/app.zs", 3), Ref("foo", "pkg/lib/foo", "/a/app.zs", 4)]
        );

        var refs = index.FindReferences("pkg/lib/foo", "foo", "/a/lib.zs");

        // Two cross-file uses plus the same-file declaration occurrence.
        Assert.Equal(3, refs.Count);
        Assert.Contains(refs, r => r.File == "/a/app.zs" && r.Span.Line == 3);
        Assert.Contains(refs, r => r.File == "/a/app.zs" && r.Span.Line == 4);
        Assert.Contains(refs, r => r.File == "/a/lib.zs");
    }

    [Fact]
    public void UpdateFile_ReplacesStaleSlice()
    {
        var index = new WorkspaceIndex();
        index.UpdateFile("/a/app.zs", [], [Ref("foo", "pkg/lib/foo", "/a/app.zs", 3)]);
        // Re-index the same file with the reference removed.
        index.UpdateFile("/a/app.zs", [], []);

        var refs = index.FindReferences("pkg/lib/foo", "foo", "/a/lib.zs");

        Assert.Empty(refs);
    }

    [Fact]
    public void RemoveFile_DropsDefinitions()
    {
        var index = new WorkspaceIndex();
        index.UpdateFile(
            "/a/lib.zs",
            [Def("pkg/lib", "foo", "/a/lib.zs", SymbolKind.Function)],
            []
        );

        index.RemoveFile("/a/lib.zs");

        Assert.Empty(index.ResolveDefinition("pkg/lib/foo", "foo"));
        Assert.False(index.Contains("/a/lib.zs"));
    }

    [Fact]
    public void SearchSymbols_SubsequenceMatch()
    {
        var index = new WorkspaceIndex();
        index.UpdateFile(
            "/a/lib.zs",
            [
                Def("pkg/lib", "list-map", "/a/lib.zs", SymbolKind.Function),
                Def("pkg/lib", "list-fold", "/a/lib.zs", SymbolKind.Function),
                Def("pkg/lib", "Widget", "/a/lib.zs", SymbolKind.Record),
            ],
            []
        );

        var byExact = index.SearchSymbols("list-map");
        Assert.Contains(byExact, d => d.BareName == "list-map");

        var bySubsequence = index.SearchSymbols("lm"); // l..m subsequence
        Assert.Contains(bySubsequence, d => d.BareName == "list-map");

        var all = index.SearchSymbols("");
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void IndexedDefinitions_CarryParamNames()
    {
        var src = """
            (module test)
            (define (scale [factor : Int] [amount : Int]) : Int (* factor amount))
            (define (sum-all [first : Int] [rest : Int ...]) : Int first)
            """;
        var (svc, uri) = TestFixtures.LspTestSession.Open(src);
        var file = OmniSharp
            .Extensions.LanguageServer.Protocol.DocumentUri.Parse(uri)
            .GetFileSystemPath();

        var scale = svc.Index.DefinitionInFile(file, "scale");
        Assert.NotNull(scale);
        Assert.Equal(["factor", "amount"], scale!.ParamNames);
        Assert.False(scale.IsVariadic);

        var sumAll = svc.Index.DefinitionInFile(file, "sum-all");
        Assert.NotNull(sumAll);
        Assert.Equal(["first", "rest"], sumAll!.ParamNames);
        Assert.True(sumAll.IsVariadic);
    }

    [Fact]
    public void FindReferences_IncludesPatternConstructorUses()
    {
        var src = """
            (module test)
            (define-union Shape (Circle [r : Int]) (Square [s : Int]))
            (define (area [sh : Shape]) : Int
              (match sh
                [(Circle r) (* r r)]
                [(Square s) (* s s)]))
            """;
        var (svc, uri) = TestFixtures.LspTestSession.Open(src);
        var file = OmniSharp
            .Extensions.LanguageServer.Protocol.DocumentUri.Parse(uri)
            .GetFileSystemPath();

        var refs = svc.Index.FindReferences(null, "Circle", file);

        // The declaration's case name plus the pattern use in 'area' — pattern case
        // names are indexed like constructor call sites.
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void TypeUses_AreIndexedWithContainerAttribution()
    {
        var src = """
            (module test)
            (define-record Point [x : Int])
            (define (f [p : Point]) : Point p)
            (define g 1)
            """;
        var (svc, uri) = TestFixtures.LspTestSession.Open(src);
        var file = OmniSharp
            .Extensions.LanguageServer.Protocol.DocumentUri.Parse(uri)
            .GetFileSystemPath();

        // Both annotation sites are indexed as type uses, each attributed to 'f'.
        var typeRefs = svc.Index.FindTypeReferences("Point");
        Assert.Equal(2, typeRefs.Count);
        Assert.All(typeRefs, r => Assert.True(r.IsTypeUse));
        Assert.All(typeRefs, r => Assert.Equal(file, r.File));
        Assert.All(typeRefs, r => Assert.Null(r.QualifiedKey));
        Assert.All(typeRefs, r => Assert.Equal("f", r.ContainingDefinition));

        // The declaration name itself is a Name occurrence, not a type use.
        Assert.DoesNotContain(typeRefs, r => r.Span.Line == 2);
    }

    [Fact]
    public void FindTypeReferences_MatchesBareNameAcrossFiles()
    {
        var index = new WorkspaceIndex();
        index.UpdateFile(
            "/a/lib.zs",
            [
                new IndexedDefinition(
                    "pkg/lib/Widget",
                    "Widget",
                    new SourceSpan("/a/lib.zs", 2, 20, 6),
                    SymbolKind.Record,
                    "pkg/lib"
                ),
            ],
            []
        );
        index.UpdateFile(
            "/a/app.zs",
            [],
            [
                new IndexedReference(
                    "Widget",
                    null,
                    new SourceSpan("/a/app.zs", 3, 20, 6),
                    "run",
                    IsTypeUse: true
                ),
                // A same-named Name occurrence is not a type use.
                new IndexedReference("Widget", null, new SourceSpan("/a/app.zs", 4, 5, 6), null),
            ]
        );

        var typeRefs = index.FindTypeReferences("Widget");
        var theRef = Assert.Single(typeRefs);
        Assert.Equal("/a/app.zs", theRef.File);
        Assert.Equal(3, theRef.Span.Line);

        // Re-indexing the file without the type use drops it.
        index.UpdateFile("/a/app.zs", [], []);
        Assert.Empty(index.FindTypeReferences("Widget"));
    }

    [Fact]
    public void CallHierarchy_DoesNotCountTypeUsesAsCalls()
    {
        var src = """
            (module test)
            (define-record Point [x : Int])
            (define (f [p : Point]) : Point p)
            (define (make) : Point (Point 1))
            """;
        var (svc, uri) = TestFixtures.LspTestSession.Open(src);
        var file = OmniSharp
            .Extensions.LanguageServer.Protocol.DocumentUri.Parse(uri)
            .GetFileSystemPath();
        var def = svc.Index.DefinitionInFile(file, "Point")!;

        // 'f' only annotates with Point (no constructor call): a type use is not a call.
        var incoming = svc.Index.IncomingCalls(def.QualifiedKey, def.BareName, def.File, def.Span);
        Assert.DoesNotContain(incoming, c => c.Caller.BareName == "f");

        // 'make' calls the constructor: it is a caller.
        Assert.Contains(incoming, c => c.Caller.BareName == "make");
    }

    [Fact]
    public void IncomingCalls_AttributeNestedLambdasToTheEnclosingDefine()
    {
        var src = """
            (module test)
            (define (outer) : Int ((lambda (x) (helper x)) 1))
            (define (helper [n : Int]) : Int n)
            (helper 5)
            """;
        var (svc, uri) = TestFixtures.LspTestSession.Open(src);
        var file = OmniSharp
            .Extensions.LanguageServer.Protocol.DocumentUri.Parse(uri)
            .GetFileSystemPath();
        var def = svc.Index.DefinitionInFile(file, "helper")!;

        var incoming = svc.Index.IncomingCalls(def.QualifiedKey, def.BareName, def.File, def.Span);

        // The call inside the lambda attributes to 'outer'; the module-scope
        // (helper 5) has no containing definition and is dropped.
        var (caller, spans) = Assert.Single(incoming);
        Assert.Equal("outer", caller.BareName);
        Assert.Single(spans);
    }
}
