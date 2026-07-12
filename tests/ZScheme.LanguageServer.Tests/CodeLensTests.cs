using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class CodeLensTests
{
    [Fact]
    public void FunctionUsedTwice_ShowsTwoReferences()
    {
        var source = """
            (define (helper [n : Int]) : Int n)
            (define a (helper 1))
            (define b (helper 2))
            """;
        var (service, uri) = LspTestSession.Open(source);
        var filePath = new Uri(uri).LocalPath;

        var lenses = CodeLensHandler.Compute(service.Index, filePath, DocumentUri.Parse(uri));

        var helperLens = lenses.First(l => l.Range.Start.Line == 0);
        Assert.Equal("2 references", helperLens.Command!.Title);
    }

    [Fact]
    public void UnusedDefinition_ShowsZeroReferences()
    {
        var source = "(define (lonely) 1)";
        var (service, uri) = LspTestSession.Open(source);
        var filePath = new Uri(uri).LocalPath;

        var lenses = CodeLensHandler.Compute(service.Index, filePath, DocumentUri.Parse(uri));

        var lens = Assert.Single(lenses);
        Assert.Equal("0 references", lens.Command!.Title);
    }

    [Fact]
    public void SingleReference_UsesSingularTitle()
    {
        var source = """
            (define (once) 1)
            (define x (once))
            """;
        var (service, uri) = LspTestSession.Open(source);
        var filePath = new Uri(uri).LocalPath;

        var lenses = CodeLensHandler.Compute(service.Index, filePath, DocumentUri.Parse(uri));

        var lens = lenses.First(l => l.Range.Start.Line == 0);
        Assert.Equal("1 reference", lens.Command!.Title);
    }

    [Fact]
    public void UnionAndCases_EachGetALens()
    {
        var source = """
            (define-union Shape
              (Circle [r : Int]))
            (define c (Circle 1))
            """;
        var (service, uri) = LspTestSession.Open(source);
        var filePath = new Uri(uri).LocalPath;

        var lenses = CodeLensHandler.Compute(service.Index, filePath, DocumentUri.Parse(uri));

        // Shape, Circle (case), and c each have a lens.
        Assert.True(lenses.Count >= 3);
    }

    [Fact]
    public void Lens_IsClickable_WithShowReferencesCommand()
    {
        var source = """
            (define (helper [n : Int]) : Int n)
            (define a (helper 1))
            """;
        var (service, uri) = LspTestSession.Open(source);
        var filePath = new Uri(uri).LocalPath;

        var lenses = CodeLensHandler.Compute(service.Index, filePath, DocumentUri.Parse(uri));

        var lens = lenses.First(l => l.Range.Start.Line == 0);
        Assert.Equal("editor.action.showReferences", lens.Command!.Name);

        // Arguments serialize with LSP casing: [uriString, position, locations].
        var args = lens.Command.Arguments!;
        Assert.Equal(3, args.Count);
        Assert.Equal(uri, args[0]!.ToString());
        Assert.NotNull(args[1]!["line"]);
        Assert.NotNull(args[1]!["character"]);
        var location = args[2]!.First!;
        Assert.NotNull(location["uri"]);
        Assert.NotNull(location["range"]!["start"]!["line"]);
    }

    [Fact]
    public void CrossFileReferences_AreCounted()
    {
        using var ws = new TempPackageWorkspace(
            "clpkg",
            new Dictionary<string, string>
            {
                ["lib.zs"] = """
                    (module lib)
                    (define (shared [n : Int]) : Int n)
                    (export shared)
                    """,
                ["app.zs"] = """
                    (module app)
                    (import clpkg/lib)
                    (define x (shared 1))
                    (define y (shared 2))
                    """,
            }
        );
        ws.Open("lib.zs");
        ws.Open("app.zs");

        var lenses = CodeLensHandler.Compute(
            ws.Service.Index,
            ws.PathOf("lib.zs"),
            DocumentUri.FromFileSystemPath(ws.PathOf("lib.zs"))
        );

        var sharedLens = lenses.First(l => l.Range.Start.Line == 1);
        Assert.Equal("2 references", sharedLens.Command!.Title);
    }
}
