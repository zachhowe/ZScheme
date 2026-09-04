using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Formatter;

namespace ZScheme.Formatter.Tests;

public class ImportMergerTests
{
    [Fact]
    public void SingleImport_StaysSingle()
    {
        var sexprs = ParseImports("(import a)");
        var result = ImportMerger.MergeImports(sexprs);
        Assert.Single(result);
        var list = (SExpr.SList)result[0];
        Assert.Equal(2, list.Items.Count); // import keyword + 1 name
    }

    [Fact]
    public void MultipleImports_Merged()
    {
        var sexprs = ParseImports("(import a)\n(import b)\n(import c)");
        var result = ImportMerger.MergeImports(sexprs);
        Assert.Single(result);
        var list = (SExpr.SList)result[0];
        Assert.Equal(4, list.Items.Count); // import + 3 names
    }

    [Fact]
    public void ImportsMixedWithOtherForms_Preserved()
    {
        var sexprs = ParseImports("(import a)\n(define x 1)\n(import b)");
        var result = ImportMerger.MergeImports(sexprs);
        Assert.Equal(3, result.Count);
    }

    private static List<SExpr> ParseImports(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        return parser.ParseAll();
    }
}
