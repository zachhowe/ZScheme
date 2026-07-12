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
        index.UpdateFile(
            "/a/app.zs",
            [],
            [Ref("foo", "pkg/lib/foo", "/a/app.zs", 3)]
        );
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
        var file = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri
            .Parse(uri)
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
}
