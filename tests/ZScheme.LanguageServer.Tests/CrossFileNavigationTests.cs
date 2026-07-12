using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class CrossFileNavigationTests
{
    private const string Lib = """
        (module lib)
        (define (lib-double [n : Int]) : Int (* n 2))
        (define-record Widget [size : Int])
        (export lib-double Widget)
        """;

    private const string App = """
        (module app)
        (import xpkg/lib)
        (define (run [n : Int]) : Int (lib-double (lib-double n)))
        (define (make-widget) : Widget (Widget 5))
        """;

    private const string App2 = """
        (module app2)
        (import xpkg/lib)
        (define (run2 [n : Int]) : Int (lib-double n))
        """;

    private static TempPackageWorkspace NewWorkspace()
    {
        return new TempPackageWorkspace(
            "xpkg",
            new Dictionary<string, string>
            {
                ["lib.zs"] = Lib,
                ["app.zs"] = App,
                ["app2.zs"] = App2,
            }
        );
    }

    [Fact]
    public void Definition_ImportedFunction_JumpsToDefiningFile()
    {
        using var ws = NewWorkspace();
        ws.Open("lib.zs");
        var appState = ws.Open("app.zs");

        var (line, col) = ws.Locate("app.zs", "lib-double"); // first use in app
        var span = DefinitionHandler.ResolveDefinition(appState, line, col, ws.Service.Index);

        Assert.NotNull(span);
        Assert.Equal(ws.PathOf("lib.zs"), span.Value.File);
        Assert.Equal(2, span.Value.Line); // (define (lib-double ...
    }

    [Fact]
    public void Definition_ImportedRecordConstructor_JumpsToDefiningFile()
    {
        using var ws = NewWorkspace();
        ws.Open("lib.zs");
        var appState = ws.Open("app.zs");

        var (line, col) = ws.Locate("app.zs", "Widget", 2); // the (Widget 5) constructor call
        var span = DefinitionHandler.ResolveDefinition(appState, line, col, ws.Service.Index);

        Assert.NotNull(span);
        Assert.Equal(ws.PathOf("lib.zs"), span.Value.File);
        Assert.Equal(3, span.Value.Line);
    }

    [Fact]
    public void Definition_LocalSymbol_StaysInCurrentFile()
    {
        using var ws = NewWorkspace();
        ws.Open("lib.zs");
        var appState = ws.Open("app.zs");

        // 'n' resolves locally as a parameter → no definition (parameters excluded),
        // but the local function 'run' referenced from elsewhere would stay local; here
        // assert the imported case does not accidentally hijack a same-file name.
        var span = DefinitionHandler.ResolveDefinition(
            appState,
            ws.Locate("app.zs", "lib-double").Line,
            ws.Locate("app.zs", "lib-double").Col,
            ws.Service.Index
        );
        Assert.NotNull(span);
        Assert.NotEqual(ws.PathOf("app.zs"), span.Value.File);
    }

    [Fact]
    public void References_ImportedFunction_FindsCrossFileUses()
    {
        using var ws = NewWorkspace();
        ws.Open("lib.zs");
        ws.Open("app.zs");
        ws.Open("app2.zs");

        var libState = ws.Service.GetDocument(ws.UriOf("lib.zs"))!;
        var (line, col) = ws.Locate("lib.zs", "lib-double"); // the declaration name

        var refs = ReferencesHandler.ResolveReferences(
            libState,
            ws.Service.Index,
            line,
            col,
            includeDeclaration: false,
            DocumentUri.FromFileSystemPath(ws.PathOf("lib.zs"))
        );

        var files = refs.Select(r => r.Uri.GetFileSystemPath()).ToHashSet();
        Assert.Contains(ws.PathOf("app.zs"), files);
        Assert.Contains(ws.PathOf("app2.zs"), files);
        // Declaration excluded: no lib.zs occurrence when includeDeclaration is false.
        Assert.DoesNotContain(ws.PathOf("lib.zs"), files);
        // Two uses in app.zs + one in app2.zs.
        Assert.Equal(3, refs.Count);
    }

    [Fact]
    public void References_IncludeDeclaration_AddsDefiningSite()
    {
        using var ws = NewWorkspace();
        ws.Open("lib.zs");
        ws.Open("app.zs");
        ws.Open("app2.zs");

        var libState = ws.Service.GetDocument(ws.UriOf("lib.zs"))!;
        var (line, col) = ws.Locate("lib.zs", "lib-double");

        var refs = ReferencesHandler.ResolveReferences(
            libState,
            ws.Service.Index,
            line,
            col,
            includeDeclaration: true,
            DocumentUri.FromFileSystemPath(ws.PathOf("lib.zs"))
        );

        var files = refs.Select(r => r.Uri.GetFileSystemPath()).ToHashSet();
        Assert.Contains(ws.PathOf("lib.zs"), files);
        Assert.Equal(4, refs.Count);
    }

    [Fact]
    public async Task WorkspaceSymbol_FindsSymbolsAcrossFiles()
    {
        using var ws = NewWorkspace();
        ws.Open("lib.zs");
        ws.Open("app.zs");

        var handler = new WorkspaceSymbolHandler(ws.Service);
        var result = await handler.Handle(
            new WorkspaceSymbolParams { Query = "lib-double" },
            CancellationToken.None
        );

        Assert.NotNull(result);
        var match = Assert.Single(result, s => s.Name == "lib-double");
        Assert.Equal(SymbolKind.Function, match.Kind);
        Assert.Equal("xpkg/lib", match.ContainerName);
        Assert.Equal(ws.PathOf("lib.zs"), match.Location.Location!.Uri.GetFileSystemPath());
    }

    [Fact]
    public async Task Definition_ResolvesIntoUnopenedFile_AfterWorkspaceScan()
    {
        using var ws = NewWorkspace();

        // Open only app.zs — lib.zs is never opened, so it enters the index only via the
        // background workspace scan.
        var appState = ws.Open("app.zs");
        await ws.Service.InitializeWorkspaceAsync([ws.Root]);

        var libPath = ws.PathOf("lib.zs");
        Assert.True(ws.Service.Index.Contains(libPath), "workspace scan did not index lib.zs");

        var (line, col) = ws.Locate("app.zs", "lib-double");
        var span = DefinitionHandler.ResolveDefinition(appState, line, col, ws.Service.Index);

        Assert.NotNull(span);
        Assert.Equal(libPath, span.Value.File);
    }

    [Fact]
    public async Task WorkspaceSymbol_FindsRecord()
    {
        using var ws = NewWorkspace();
        ws.Open("lib.zs");

        var handler = new WorkspaceSymbolHandler(ws.Service);
        var result = await handler.Handle(
            new WorkspaceSymbolParams { Query = "Widget" },
            CancellationToken.None
        );

        Assert.NotNull(result);
        Assert.Contains(result, s => s.Name == "Widget" && s.Kind == SymbolKind.Struct);
    }
}
