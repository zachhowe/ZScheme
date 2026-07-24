using System.Runtime.CompilerServices;
using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Handlers;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class DefinitionTests
{
    private static (AnalysisService Service, string Uri) NewSession(
        string source,
        [CallerMemberName] string testName = ""
    )
    {
        return LspTestSession.Open(source, testName: testName);
    }

    [Fact]
    public void Definition_ReferenceToTopLevelFunction_ResolvesToNameSpan()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            (define (twice [n : Int]) : Int (square n))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // Cursor on the call to "square" inside twice's body, line 3.
        var span = DefinitionHandler.ResolveDefinition(state, 3, 35);

        Assert.NotNull(span);
        Assert.Equal(2, span.Value.Line);
        // The definition span should target "square" specifically.
        Assert.Equal(6, span.Value.Length);
    }

    [Fact]
    public void Definition_ReferenceToDefineValue_ResolvesToValueNameSpan()
    {
        var src = """
            (module test)
            (define answer 42)
            (define (use-it) : Int answer)
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        var span = DefinitionHandler.ResolveDefinition(state, 3, 25);

        Assert.NotNull(span);
        Assert.Equal(2, span.Value.Line);
        Assert.Equal(6, span.Value.Length);
    }

    [Fact]
    public void Definition_ReferenceToRecordType_ResolvesToRecordSpan()
    {
        var src = """
            (module test)
            (define-record Point [x : Int] [y : Int])
            (define (origin) : Point (Point 0 0))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // "Point" constructor reference inside origin's body.
        var span = DefinitionHandler.ResolveDefinition(state, 3, 27);

        Assert.NotNull(span);
        Assert.Equal(2, span.Value.Line);
    }

    [Fact]
    public void Definition_ReferenceToUnionCase_ResolvesToCaseSpan()
    {
        var src = """
            (module test)
            (define-union Shape (Circle [r : Int]) (Square [s : Int]))
            (define (mk) : Shape (Circle 5))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // Cursor on "Circle" call, line 3.
        var span = DefinitionHandler.ResolveDefinition(state, 3, 23);

        Assert.NotNull(span);
        Assert.Equal(2, span.Value.Line);
    }

    [Fact]
    public void Definition_OnParameterUse_ResolvesToParameterName()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // Cursor on the "x" reference in the body.
        var span = DefinitionHandler.ResolveDefinition(state, 2, 37);

        Assert.NotNull(span);
        // The name atom inside [x : Int], not the whole bracket.
        Assert.Equal(2, span.Value.Line);
        Assert.Equal(18, span.Value.Column);
        Assert.Equal(1, span.Value.Length);
    }

    [Fact]
    public void Definition_OnParameterBindingSite_ResolvesToItself()
    {
        var src = """
            (module test)
            (define (square [x : Int]) : Int (* x x))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        var span = DefinitionHandler.ResolveDefinition(state, 2, 18);

        Assert.NotNull(span);
        Assert.Equal(2, span.Value.Line);
        Assert.Equal(18, span.Value.Column);
    }

    [Fact]
    public void Definition_OnLambdaParameter_ResolvesToParameterName()
    {
        var src = """
            (module test)
            (define (make-adder [n : Int]) : (Int -> Int)
              (lambda (x) (+ n x)))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // "x" inside the lambda body binds to the lambda's parameter on line 3, not to
        // the enclosing define's parameter list.
        var (line, col) = LspTestSession.Locate(src, "x)))");
        var span = DefinitionHandler.ResolveDefinition(state, line, col);

        Assert.NotNull(span);
        Assert.Equal(3, span.Value.Line);
        Assert.Equal(12, span.Value.Column);
    }

    [Fact]
    public void Definition_OnLetVariableUse_ResolvesToBindingNameNotForm()
    {
        var src = """
            (module test)
            (define (f [x : Int]) : Int
              (let ([y (* x 2)])
                (+ y x)))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        var (line, col) = LspTestSession.Locate(src, "y x)))");
        var span = DefinitionHandler.ResolveDefinition(state, line, col);

        Assert.NotNull(span);
        // The bound name atom on line 3, not the "(let" form start (column 3).
        Assert.Equal(3, span.Value.Line);
        Assert.Equal(10, span.Value.Column);
        Assert.Equal(1, span.Value.Length);
    }

    [Fact]
    public void Definition_OnLetVariable_PrefersOwnBinderOverSameNameInSiblingFunction()
    {
        // Regression: NameToDefinition was keyed by bare name file-wide (first wins), so
        // "y" inside g resolved to f's binding.
        var src = """
            (module test)
            (define (f [x : Int]) : Int
              (let ([y (* x 2)])
                (+ y x)))
            (define (g [x : Int]) : Int
              (let ([y (+ x 100)])
                (- y x)))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        var (line, col) = LspTestSession.Locate(src, "y x)))", occurrence: 2);
        var span = DefinitionHandler.ResolveDefinition(state, line, col);

        Assert.NotNull(span);
        Assert.Equal(6, span.Value.Line); // g's binding, not f's on line 3
    }

    [Fact]
    public void Definition_OnShadowingLetVariable_ResolvesToInnermostBinder()
    {
        var src = """
            (module test)
            (define (f [n : Int]) : Int
              (let ([v (* n 2)])
                (let ([v (+ v 1)])
                  (* v v))))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        var (line, col) = LspTestSession.Locate(src, "v v))))");
        var span = DefinitionHandler.ResolveDefinition(state, line, col);

        Assert.NotNull(span);
        Assert.Equal(4, span.Value.Line); // the inner binding, not the outer one on line 3
    }

    [Fact]
    public void Definition_OnMatchPatternVariable_ResolvesToPatternBinding()
    {
        var src = """
            (module test)
            (define-union Shape (Circle [r : Int]) (Square [s : Int]))
            (define (area [sh : Shape]) : Int
              (match sh
                [(Circle r) (* r r)]
                [(Square s) (* s s)]))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // The "r" in the arm body binds to the "r" in the pattern on the same line.
        var (line, col) = LspTestSession.Locate(src, "r r)]");
        var span = DefinitionHandler.ResolveDefinition(state, line, col);

        Assert.NotNull(span);
        Assert.Equal(5, span.Value.Line);
        Assert.Equal(14, span.Value.Column); // the "r" inside (Circle r)
    }

    [Fact]
    public void Definition_OnImportClrAliasUse_ResolvesToAliasDeclaration()
    {
        var src = """
            (module test)
            (import-clr
              [println System.Console/WriteLine])
            (define (shout [s : String]) : Unit (println s))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        var (line, col) = LspTestSession.Locate(src, "println", occurrence: 2);
        var span = DefinitionHandler.ResolveDefinition(state, line, col);

        Assert.NotNull(span);
        // The alias atom inside the bracket, not the whole [println …] bracket (column 3).
        Assert.Equal(3, span.Value.Line);
        Assert.Equal(4, span.Value.Column);
        Assert.Equal("println".Length, span.Value.Length);
    }

    [Fact]
    public void Definition_OnNonNameNode_ReturnsNull()
    {
        var src = """
            (module test)
            (define n 42)
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // Cursor on the integer literal "42" — not a Name node.
        var span = DefinitionHandler.ResolveDefinition(state, 2, 12);

        Assert.Null(span);
    }

    [Fact]
    public void Definition_OnUndefinedName_ReturnsNull()
    {
        var src = """
            (module test)
            (define (use-undef) : Int does-not-exist)
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        var span = DefinitionHandler.ResolveDefinition(state, 2, 27);

        Assert.Null(span);
    }

    [Fact]
    public void Definition_NullAst_ReturnsNull()
    {
        // Construct a state with no AST (e.g. a fresh open with a fully-broken file
        // and no prior good state).
        var state = new DocumentState(
            "file:///empty.zs",
            1,
            "(((",
            null,
            new DiagnosticBag(),
            [],
            new Dictionary<string, SymbolInfo>(),
            new Dictionary<string, AstNode.TypeAliasDecl>()
        );

        var span = DefinitionHandler.ResolveDefinition(state, 1, 1);

        Assert.Null(span);
    }

    [Fact]
    public void Definition_ReferenceAcrossMultilineDefine_ResolvesToNameSpan()
    {
        // Regression for the multi-line name-span fix: the call site below should
        // resolve to "square" on line 2, not the form span which only covers part
        // of the first line.
        var src = """
            (module test)
            (define (square [x : Int]) : Int
              (* x x))
            (define (twice [n : Int]) : Int (square n))
            """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        var span = DefinitionHandler.ResolveDefinition(state, 4, 35);

        Assert.NotNull(span);
        Assert.Equal(2, span.Value.Line);
        Assert.Equal(6, span.Value.Length);
    }
}
