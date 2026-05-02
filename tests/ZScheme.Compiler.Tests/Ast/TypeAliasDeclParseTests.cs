using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Ast;

public class TypeAliasDeclParseTests
{
    private static (AstNode.Program Program, DiagnosticBag Diagnostics) BuildWithDiagnostics(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);
        return (program, diag);
    }

    private static AstNode.TypeAliasDecl ParseAlias(string source)
    {
        var (program, diag) = BuildWithDiagnostics(source);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return Assert.IsType<AstNode.TypeAliasDecl>(program.TopLevelForms[0]);
    }

    [Fact]
    public void GenericTwoParam_ParsesAsAlias()
    {
        var alias = ParseAlias(
            "(define-type-alias (MyDict ^k ^v) System.Collections.Generic.Dictionary)");
        Assert.Equal("MyDict", alias.AliasName);
        Assert.Equal(new[] { "^k", "^v" }, alias.TypeParams);
        Assert.Equal("System.Collections.Generic.Dictionary", alias.ClrTarget);
        Assert.False(alias.IsArray);
        Assert.Null(alias.AssemblyHint);
    }

    [Fact]
    public void GenericOneParam_ParsesAsAlias()
    {
        var alias = ParseAlias(
            "(define-type-alias (MyList ^a) System.Collections.Generic.List)");
        Assert.Equal("MyList", alias.AliasName);
        Assert.Single(alias.TypeParams);
        Assert.Equal("^a", alias.TypeParams[0]);
        Assert.False(alias.IsArray);
    }

    [Fact]
    public void ArrayKeyword_SetsIsArray()
    {
        var alias = ParseAlias("(define-type-alias (MyArr ^a) :array)");
        Assert.True(alias.IsArray);
        Assert.Equal("MyArr", alias.AliasName);
        Assert.Single(alias.TypeParams);
        Assert.Equal("", alias.ClrTarget);
    }

    [Fact]
    public void ArrayWithMultipleTypeParams_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics(
            "(define-type-alias (Bad ^a ^b) :array)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics,
            d => d.Message.Contains("requires exactly one type parameter"));
    }

    [Fact]
    public void AssemblyHint_FromKeyword_Parses()
    {
        var alias = ParseAlias(
            "(define-type-alias (MyMap ^k ^v) System.Collections.Immutable.ImmutableDictionary :from \"System.Collections.Immutable\")");
        Assert.Equal("System.Collections.Immutable", alias.AssemblyHint);
        Assert.Equal("System.Collections.Immutable.ImmutableDictionary", alias.ClrTarget);
    }

    [Fact]
    public void DuplicateTypeParam_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics(
            "(define-type-alias (Bad ^a ^a) System.Foo)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("duplicate type parameter"));
    }

    [Fact]
    public void LowercaseTypeParam_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics(
            "(define-type-alias (Bad k v) System.Foo)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics,
            d => d.Message.Contains("type params must start with '^'"));
    }

    [Fact]
    public void LowercaseAliasName_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics(
            "(define-type-alias (badName ^a) System.Foo)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics,
            d => d.Message.Contains("must start with an uppercase letter"));
    }

    [Fact]
    public void BareName_ZeroArity_Parses()
    {
        var alias = ParseAlias("(define-type-alias MyType System.DateTime)");
        Assert.Equal("MyType", alias.AliasName);
        Assert.Empty(alias.TypeParams);
        Assert.Equal("System.DateTime", alias.ClrTarget);
        Assert.False(alias.IsArray);
    }

    [Fact]
    public void TooFewArguments_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(define-type-alias Foo)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics,
            d => d.Message.Contains("requires a name") ||
                 d.Message.Contains("CLR target"));
    }

    [Fact]
    public void TrailingItems_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics(
            "(define-type-alias (Foo ^a) System.Foo extra-thing)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics,
            d => d.Message.Contains("unexpected trailing"));
    }
}
