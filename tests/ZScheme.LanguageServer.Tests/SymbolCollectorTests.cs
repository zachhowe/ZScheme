using System.Runtime.CompilerServices;
using Xunit;
using ZScheme.LanguageServer.Analysis;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

public sealed class SymbolCollectorTests
{
    private static IReadOnlyList<SymbolInfo> Symbols(string src, [CallerMemberName] string testName = "")
    {
        var (svc, uri) = LspTestSession.Open(src, testName: testName);
        return svc.GetDocument(uri)!.Symbols;
    }

    private static IReadOnlyDictionary<string, SymbolInfo> NameToDef(
        string src, [CallerMemberName] string testName = "")
    {
        var (svc, uri) = LspTestSession.Open(src, testName: testName);
        return svc.GetDocument(uri)!.NameToDefinition;
    }

    [Fact]
    public void Collect_DefineYieldsFunctionSymbol()
    {
        var symbols = Symbols("""
            (module test)
            (define (id [x : Int]) : Int x)
            """);

        Assert.Contains(symbols, s => s.Name == "id" && s.Kind == SymbolKind.Function);
    }

    [Fact]
    public void Collect_DefineValueYieldsVariableSymbol()
    {
        var symbols = Symbols("""
            (module test)
            (define answer 42)
            """);

        Assert.Contains(symbols, s => s.Name == "answer" && s.Kind == SymbolKind.Variable);
    }

    [Fact]
    public void Collect_RecordAndUnionAndCases()
    {
        var symbols = Symbols("""
            (module test)
            (record Point [x : Int] [y : Int])
            (union Shape (Circle [r : Int]) (Square [s : Int]))
            """);

        Assert.Contains(symbols, s => s.Name == "Point" && s.Kind == SymbolKind.Record);
        Assert.Contains(symbols, s => s.Name == "Shape" && s.Kind == SymbolKind.Union);
        Assert.Contains(symbols, s => s.Name == "Circle" && s.Kind == SymbolKind.UnionCase);
        Assert.Contains(symbols, s => s.Name == "Square" && s.Kind == SymbolKind.UnionCase);
    }

    [Fact]
    public void Collect_ParameterEmittedSeparately()
    {
        var symbols = Symbols("""
            (module test)
            (define (id [some-arg : Int]) : Int some-arg)
            """);

        Assert.Contains(symbols, s => s.Name == "some-arg" && s.Kind == SymbolKind.Parameter);
    }

    [Fact]
    public void Collect_NameToDefinitionExcludesParameters()
    {
        var nameToDef = NameToDef("""
            (module test)
            (define (id [some-arg : Int]) : Int some-arg)
            """);

        Assert.True(nameToDef.ContainsKey("id"));
        Assert.False(nameToDef.ContainsKey("some-arg"));
    }

    [Fact]
    public void Collect_ModuleBodyIsRecursed()
    {
        var symbols = Symbols("""
            (module my-mod)
            (define (inside-mod) : Int 1)
            """);

        Assert.Contains(symbols, s => s.Name == "my-mod" && s.Kind == SymbolKind.Module);
        Assert.Contains(symbols, s => s.Name == "inside-mod" && s.Kind == SymbolKind.Function);
    }

    [Fact]
    public void Collect_MultiLineDefinePrefersNameSpan()
    {
        var symbols = Symbols("""
            (module test)
            (define (square [x : Int]) : Int
              (* x x))
            """);

        var sq = symbols.First(s => s.Name == "square" && s.Kind == SymbolKind.Function);
        // Name span targets "square" (length 6) on line 2, not the form span.
        Assert.Equal(2, sq.DefinitionSpan.Line);
        Assert.Equal(6, sq.DefinitionSpan.Length);
    }
}
