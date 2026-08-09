using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

/// <summary>
///     A primitive's CLR full name and its ZScheme keyword are one type. The split used to live
///     in the representation rather than the naming — <c>String</c> parsed as a
///     <see cref="ZType.ZPrimitiveType" /> while <c>System.String</c> fell through to a
///     <see cref="ZType.ZNamedType" />, and <c>Unifier</c> has no arm bridging the two — so an
///     interop signature could not spell a primitive out alongside its qualified neighbours.
/// </summary>
public class PrimitiveTypeNamesTests
{
    private static DiagnosticBag Compile(string source)
    {
        var diag = new DiagnosticBag();
        var tokens = new Lexer(source, "test.zs", diag).Tokenize();
        var sexprs = new SExprParser(tokens, diag).ParseAll();
        var program = new AstBuilder(diag).BuildProgram(sexprs);

        var env = TypeEnv.CreateRoot();
        var inferer = new TypeInferer(diag);
        inferer.Infer(program, env);
        inferer.Resolve(program);
        return diag;
    }

    [Theory]
    [InlineData("System.Int32", "Int")]
    [InlineData("System.Int64", "Long")]
    [InlineData("System.Single", "Float")]
    [InlineData("System.Double", "Double")]
    [InlineData("System.Byte", "Byte")]
    [InlineData("System.Char", "Char")]
    [InlineData("System.Boolean", "Bool")]
    [InlineData("System.String", "String")]
    public void ClrFullName_AndKeyword_AreTheSameType(string clrName, string keyword)
    {
        Assert.Same(PrimitiveTypeNames.Lookup(keyword), PrimitiveTypeNames.Lookup(clrName));

        // Both directions, since a signature may qualify either half.
        Assert.False(Compile($"(module p) (define (f [x : {clrName}]) : {keyword} x)").HasErrors);
        Assert.False(Compile($"(module p) (define (g [x : {keyword}]) : {clrName} x)").HasErrors);
    }

    /// <summary>
    ///     A void-returning interop method annotated the way .NET spells it, rather than the way
    ///     ZScheme does.
    /// </summary>
    [Fact]
    public void SystemVoid_IsUnit()
    {
        Assert.Same(ZType.Unit, PrimitiveTypeNames.Lookup("System.Void"));
    }

    /// <summary><c>ZSymbol</c> is a runtime type, so the keyword has no CLR counterpart to
    ///     alias.</summary>
    [Fact]
    public void Symbol_IsKeywordOnly()
    {
        Assert.Same(ZType.Symbol, PrimitiveTypeNames.Lookup("Symbol"));
        Assert.Null(PrimitiveTypeNames.Lookup("System.Symbol"));
    }

    /// <summary><c>Object</c> is not a primitive — <c>Unifier</c>'s boxing arms already match it
    ///     in either spelling, and mapping it here would give it a ZPrimitiveType it has no kind
    ///     for.</summary>
    [Fact]
    public void SystemObject_IsNotAPrimitive()
    {
        Assert.Null(PrimitiveTypeNames.Lookup("System.Object"));
        Assert.Null(PrimitiveTypeNames.Lookup("Object"));
    }

    [Fact]
    public void OrdinaryNamedType_IsNotAPrimitive()
    {
        Assert.Null(PrimitiveTypeNames.Lookup("System.Text.StringBuilder"));
    }

    /// <summary>The aliasing is at the leaf, so it reaches nested positions for free.</summary>
    [Fact]
    public void ClrFullName_UnifiesInsideAGenericArgument()
    {
        var diag = Compile(
            "(module p) (define (f [xs : (List System.String)]) : (List String) xs)"
        );
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    /// <summary>The trailing `?` is stripped before the atom is looked up, so a nullable CLR
    ///     spelling resolves like any other.</summary>
    [Fact]
    public void ClrFullName_UnifiesUnderNullable()
    {
        var diag = Compile("(module p) (define (f [x : System.String?]) : String? x)");
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
    }

    /// <summary>
    ///     Dropping a qualifier is only type-preserving when the qualified spelling names that
    ///     same primitive. A keyword never resolves through the namespace hints, so some other
    ///     namespace's <c>Char</c> is a different type from the primitive <c>Char</c>.
    /// </summary>
    [Theory]
    [InlineData("System.Char", "Char", true)]
    [InlineData("System.String", "String", true)]
    [InlineData("Some.Other.Ns.Char", "Char", false)]
    [InlineData("Some.Other.Ns.String", "String", false)]
    [InlineData("System.Text.StringBuilder", "StringBuilder", true)]
    public void ShorteningPreservesType_TracksTheAlias(
        string qualified,
        string shortName,
        bool preserved
    )
    {
        Assert.Equal(preserved, PrimitiveTypeNames.ShorteningPreservesType(qualified, shortName));
    }
}
