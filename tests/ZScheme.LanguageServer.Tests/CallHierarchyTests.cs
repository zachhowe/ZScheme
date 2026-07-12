using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class CallHierarchyTests
{
    private const string Src = """
        (module test)
        (define (leaf [n : Int]) : Int (* n 2))
        (define (middle [n : Int]) : Int (leaf (leaf n)))
        (define (top) : Int (middle 1))
        (leaf 9)
        """;

    private static (DocumentState State, AnalysisService Svc, string Uri) Open()
    {
        var (svc, uri) = LspTestSession.Open(Src, testName: "CallHierarchy");
        return (svc.GetDocument(uri)!, svc, uri);
    }

    [Fact]
    public void Prepare_OnFunction_ReturnsItem()
    {
        var (state, svc, uri) = Open();
        var (line, col) = LspTestSession.Locate(Src, "leaf", 1);

        var item = CallHierarchyHandler.Prepare(state, svc.Index, line, col, DocumentUri.Parse(uri));

        Assert.NotNull(item);
        Assert.Equal("leaf", item!.Name);
    }

    [Fact]
    public void Prepare_OnNonFunction_ReturnsNull()
    {
        var (state, svc, uri) = Open();
        // Column 1 of line 2 is the opening paren.
        Assert.Null(CallHierarchyHandler.Prepare(state, svc.Index, 2, 1, DocumentUri.Parse(uri)));
    }

    [Fact]
    public void Incoming_GroupsCallersAndDropsModuleScopeCalls()
    {
        var (state, svc, uri) = Open();
        var (line, col) = LspTestSession.Locate(Src, "leaf", 1);
        var item = CallHierarchyHandler.Prepare(state, svc.Index, line, col, DocumentUri.Parse(uri))!;

        var incoming = CallHierarchyHandler.Incoming(svc.Index, item);

        // 'middle' calls leaf twice; the module-scope (leaf 9) has no caller item.
        var call = Assert.Single(incoming);
        Assert.Equal("middle", call.From.Name);
        Assert.Equal(2, call.FromRanges.Count());
    }

    [Fact]
    public void Outgoing_ResolvesCallees()
    {
        var (state, svc, uri) = Open();
        var (line, col) = LspTestSession.Locate(Src, "middle", 1);
        var item = CallHierarchyHandler.Prepare(state, svc.Index, line, col, DocumentUri.Parse(uri))!;

        var outgoing = CallHierarchyHandler.Outgoing(svc.Index, item);

        var call = Assert.Single(outgoing);
        Assert.Equal("leaf", call.To.Name);
        Assert.Equal(2, call.FromRanges.Count());
    }

    [Fact]
    public void Incoming_CrossFile_FindsImportingCaller()
    {
        const string Lib = """
            (module lib)
            (define (lib-fn [n : Int]) : Int n)
            (export lib-fn)
            """;
        const string App = """
            (module app)
            (import chpkg/lib)
            (define (caller) : Int (lib-fn 1))
            """;
        using var ws = new TempPackageWorkspace(
            "chpkg",
            new Dictionary<string, string> { ["lib.zs"] = Lib, ["app.zs"] = App }
        );
        var libState = ws.Open("lib.zs");
        ws.Open("app.zs");
        var (line, col) = ws.Locate("lib.zs", "lib-fn", 1);

        var item = CallHierarchyHandler.Prepare(
            libState,
            ws.Service.Index,
            line,
            col,
            DocumentUri.FromFileSystemPath(ws.PathOf("lib.zs"))
        )!;
        var incoming = CallHierarchyHandler.Incoming(ws.Service.Index, item);

        var call = Assert.Single(incoming);
        Assert.Equal("caller", call.From.Name);
        Assert.EndsWith("app.zs", call.From.Uri.GetFileSystemPath());
    }

    [Fact]
    public void Outgoing_RecordConstructorCounts()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (make) : Point (Point 1 2))
            """;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "make", 1);
        var item = CallHierarchyHandler.Prepare(state, svc.Index, line, col, DocumentUri.Parse(uri))!;

        var outgoing = CallHierarchyHandler.Outgoing(svc.Index, item);

        var call = Assert.Single(outgoing);
        Assert.Equal("Point", call.To.Name);
    }
}
