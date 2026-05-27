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
        string source, [CallerMemberName] string testName = "")
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
    public void Definition_OnParameter_ReturnsNull()
    {
        var src = """
                  (module test)
                  (define (square [x : Int]) : Int (* x x))
                  """;
        var (svc, uri) = NewSession(src);
        var state = svc.GetDocument(uri)!;

        // Cursor on "x" reference in body — parameters are not in NameToDefinition.
        var span = DefinitionHandler.ResolveDefinition(state, 2, 37);

        Assert.Null(span);
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
            "file:///empty.zs", 1, "(((", null,
            new DiagnosticBag(),
            [], new Dictionary<string, SymbolInfo>(),
            new Dictionary<string, AstNode.TypeAliasDecl>());

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
