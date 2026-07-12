using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class TypeDefinitionTests
{
    [Fact]
    public void RecordValue_JumpsToRecordDecl()
    {
        var source = """
            (define-record Point [x : Int])
            (define p (Point 1))
            (define q p)
            """;
        var (service, uri) = LspTestSession.Open(source);
        var state = service.GetDocument(uri)!;

        var (line, col) = LspTestSession.Locate(source, "p)", 1); // `p` in (define q p)
        var spans = TypeDefinitionHandler.Resolve(state, service.Index, line, col);

        var span = Assert.Single(spans);
        Assert.Equal(1, span.Line); // (define-record Point ...
    }

    [Fact]
    public void UnionValue_JumpsToUnionDecl()
    {
        var source = """
            (define-record Ignored [z : Int])
            (define-union Shape (Circle [r : Int]))
            (define c (Circle 1))
            (define d c)
            """;
        var (service, uri) = LspTestSession.Open(source);
        var state = service.GetDocument(uri)!;

        var (line, col) = LspTestSession.Locate(source, "c)"); // `c` in (define d c)
        var spans = TypeDefinitionHandler.Resolve(state, service.Index, line, col);

        var span = Assert.Single(spans);
        Assert.Equal(2, span.Line); // (define-union Shape ...
    }

    [Fact]
    public void AnnotatedParameter_JumpsToClassDecl()
    {
        var source = """
            (define-interface IGreeter
              (Greet [] : String))
            (define-class Greeter : IGreeter
              (define (Greet) : String "hi"))
            (define (use-it [g : Greeter]) g)
            """;
        var (service, uri) = LspTestSession.Open(source);
        var state = service.GetDocument(uri)!;

        // Occurrence 1 of "g)" is inside `String))`; the body usage is occurrence 2.
        var (line, col) = LspTestSession.Locate(source, "g)", 2);
        var spans = TypeDefinitionHandler.Resolve(state, service.Index, line, col);

        var span = Assert.Single(spans);
        Assert.Equal(3, span.Line); // (define-class Greeter ...
    }

    [Fact]
    public void ImportedRecordType_JumpsAcrossFiles()
    {
        using var ws = new TempPackageWorkspace(
            "tdpkg",
            new Dictionary<string, string>
            {
                ["lib.zs"] = """
                    (module lib)
                    (define-record Widget [size : Int])
                    (define (make-widget) : Widget (Widget 5))
                    (export make-widget Widget)
                    """,
                ["app.zs"] = """
                    (module app)
                    (import tdpkg/lib)
                    (define w (make-widget))
                    (define v w)
                    """,
            }
        );
        ws.Open("lib.zs");
        var appState = ws.Open("app.zs");

        var (line, col) = ws.Locate("app.zs", "w)"); // `w` in (define v w)
        var spans = TypeDefinitionHandler.Resolve(appState, ws.Service.Index, line, col);

        var span = Assert.Single(spans);
        Assert.Equal(ws.PathOf("lib.zs"), span.File);
        Assert.Equal(2, span.Line);
    }

    [Fact]
    public void PrimitiveValue_NoTypeDefinition()
    {
        var source = "(define n 42)\n(define m n)";
        var (service, uri) = LspTestSession.Open(source);
        var state = service.GetDocument(uri)!;

        var (line, col) = LspTestSession.Locate(source, "n)");
        Assert.Empty(TypeDefinitionHandler.Resolve(state, service.Index, line, col));
    }
}
