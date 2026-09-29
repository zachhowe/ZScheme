using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class ReferencesTests
{
    // f's parameter xx is shadowed by a let of the same name; g reuses the name in an
    // unrelated function, so none of those occurrences belong to f's parameter.
    private const string ShadowedParam = """
        (module test)
        (define (f [xx : Int]) : Int
          (let ([xx (* xx 2)])
            (+ xx 1)))
        (define (g [xx : Int]) : Int xx)
        """;

    // Top-level top-v is shadowed by a let inside h; k still uses the top-level one.
    private const string ShadowedTopLevel = """
        (module test)
        (define top-v 1)
        (define (h [n : Int]) : Int
          (let ([top-v (* n 2)])
            (+ top-v n)))
        (define (k) : Int top-v)
        """;

    private static (int Line, int Col) Start(
        OmniSharp.Extensions.LanguageServer.Protocol.Models.Location l
    ) => (l.Range.Start.Line, l.Range.Start.Character);

    private static HashSet<(int Line, int Col)> Points(
        OmniSharp.Extensions.LanguageServer.Protocol.Models.Location[] locations
    ) => [.. locations.Select(Start)];

    [Fact]
    public void References_LocalParameter_ReturnsBinderAndShadowRespectingUses()
    {
        var src = ShadowedParam;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "xx", 1); // f's parameter

        var refs = ReferencesHandler.ResolveReferences(
            state,
            svc.Index,
            line,
            col,
            includeDeclaration: true,
            DocumentUri.Parse(uri)
        );

        // f's parameter binding + the use in the let's value (outer scope). The let's
        // own binding/body use and g's same-named parameter are different binders.
        var (bLine, bCol) = LspTestSession.Locate(src, "xx", 1);
        var (uLine, uCol) = LspTestSession.Locate(src, "xx", 3);
        Assert.Equal(
            new HashSet<(int, int)> { (bLine - 1, bCol - 1), (uLine - 1, uCol - 1) },
            Points(refs.ToArray())
        );
    }

    [Fact]
    public void References_LocalParameter_IncludeDeclarationFalse_ExcludesBindingSite()
    {
        var src = ShadowedParam;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "xx", 1); // f's parameter

        var refs = ReferencesHandler.ResolveReferences(
            state,
            svc.Index,
            line,
            col,
            includeDeclaration: false,
            DocumentUri.Parse(uri)
        );

        // Only the shadow-respecting use; the binding site is the declaration.
        var (uLine, uCol) = LspTestSession.Locate(src, "xx", 3);
        Assert.Equal(new HashSet<(int, int)> { (uLine - 1, uCol - 1) }, Points(refs.ToArray()));
    }

    [Fact]
    public void References_LocalParameter_FromUseSite_IncludesBindingSite()
    {
        var src = ShadowedParam;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        // Cursor on the use in the let's value — it still resolves to f's parameter.
        var (line, col) = LspTestSession.Locate(src, "xx", 3);

        var refs = ReferencesHandler.ResolveReferences(
            state,
            svc.Index,
            line,
            col,
            includeDeclaration: true,
            DocumentUri.Parse(uri)
        );

        var (bLine, bCol) = LspTestSession.Locate(src, "xx", 1);
        Assert.Equal(
            new HashSet<(int, int)> { (bLine - 1, bCol - 1), (line - 1, col - 1) },
            Points(refs.ToArray())
        );
    }

    [Fact]
    public void References_LocalPatternVariable_ReturnsBinderAndArmUses()
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

        var refs = ReferencesHandler.ResolveReferences(
            state,
            svc.Index,
            line,
            col,
            includeDeclaration: true,
            DocumentUri.Parse(uri)
        );

        // Pattern binding + the arm-body use.
        var (bLine, bCol) = LspTestSession.Locate(src, "vv", 1);
        var (uLine, uCol) = LspTestSession.Locate(src, "vv", 2);
        Assert.Equal(
            new HashSet<(int, int)> { (bLine - 1, bCol - 1), (uLine - 1, uCol - 1) },
            Points(refs.ToArray())
        );
    }

    [Fact]
    public void References_TopLevel_ExcludesOccurrencesBoundByShadowingLocal()
    {
        var src = ShadowedTopLevel;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "top-v", 1); // the declaration

        var refs = ReferencesHandler.ResolveReferences(
            state,
            svc.Index,
            line,
            col,
            includeDeclaration: true,
            DocumentUri.Parse(uri)
        );

        // Declaration + the use in k. The let-bound top-v and its body use belong to
        // the local binding, not the top-level symbol.
        var (dLine, dCol) = LspTestSession.Locate(src, "top-v", 1);
        var (kLine, kCol) = LspTestSession.Locate(src, "top-v", 4);
        Assert.Equal(
            new HashSet<(int, int)> { (dLine - 1, dCol - 1), (kLine - 1, kCol - 1) },
            Points(refs.ToArray())
        );
    }

    [Fact]
    public void References_TopLevel_IncludeDeclarationFalse_ExcludesDeclaration()
    {
        var src = ShadowedTopLevel;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "top-v", 1); // the declaration

        var refs = ReferencesHandler.ResolveReferences(
            state,
            svc.Index,
            line,
            col,
            includeDeclaration: false,
            DocumentUri.Parse(uri)
        );

        // Only the use in k — the declaration and the shadow-bound occurrences are out.
        var (kLine, kCol) = LspTestSession.Locate(src, "top-v", 4);
        Assert.Equal(new HashSet<(int, int)> { (kLine - 1, kCol - 1) }, Points(refs.ToArray()));
    }

    [Fact]
    public void References_Local_SameNameAsTopLevel_NotPollutedByTopLevel()
    {
        var src = ShadowedTopLevel;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "top-v", 2); // the let's binding

        var refs = ReferencesHandler.ResolveReferences(
            state,
            svc.Index,
            line,
            col,
            includeDeclaration: true,
            DocumentUri.Parse(uri)
        );

        // Exactly the let's binder + its body use. The same-named top-level symbol's
        // declaration and the use in k must not leak into the local's reference set.
        var (bLine, bCol) = LspTestSession.Locate(src, "top-v", 2);
        var (uLine, uCol) = LspTestSession.Locate(src, "top-v", 3);
        Assert.Equal(
            new HashSet<(int, int)> { (bLine - 1, bCol - 1), (uLine - 1, uCol - 1) },
            Points(refs.ToArray())
        );
    }

    [Fact]
    public void References_Local_SameNameAsTopLevel_IncludeDeclarationFalse_ExcludesBinder()
    {
        var src = ShadowedTopLevel;
        var (svc, uri) = LspTestSession.Open(src);
        var state = svc.GetDocument(uri)!;
        var (line, col) = LspTestSession.Locate(src, "top-v", 2); // the let's binding

        var refs = ReferencesHandler.ResolveReferences(
            state,
            svc.Index,
            line,
            col,
            includeDeclaration: false,
            DocumentUri.Parse(uri)
        );

        // Only the arm/body use; the binding site is the declaration.
        var (uLine, uCol) = LspTestSession.Locate(src, "top-v", 3);
        Assert.Equal(new HashSet<(int, int)> { (uLine - 1, uCol - 1) }, Points(refs.ToArray()));
    }
}
