using Xunit;
using ZScheme.LanguageServer.Analysis;

namespace ZScheme.LanguageServer.Tests;

public sealed class TypeNameScannerTests
{
    private static TypeNameScan Scan(string source)
    {
        return TypeNameScanner.Scan(LexicalStructure.Tokens(source));
    }

    /// <summary>Every type name written in <paramref name="source" />, as
    ///     <c>"Name/Arity"</c> in source order — compact enough to assert the whole set,
    ///     which is what catches an over-eager walk as well as a missed position.</summary>
    private static string[] Names(string source)
    {
        return [.. Scan(source).TypeNames.Select(t => $"{t.Name}/{t.Arity}")];
    }

    [Fact]
    public void ColonAnnotation_YieldsParameterAndReturnTypes()
    {
        Assert.Equal(
            ["Int", "String"],
            Scan("(define (f [x : Int]) : String x)").TypeNames.Select(t => t.Name)
        );
    }

    [Fact]
    public void TypeNameCarriesItsToken()
    {
        var src = "(define (f [x : Foo.Bar]) x)";
        var occurrence = Assert.Single(Scan(src).TypeNames);

        var (line, col) = LspTestSessionLocate(src, "Foo.Bar");
        Assert.Equal(line, occurrence.Token.Span.Line);
        Assert.Equal(col, occurrence.Token.Span.Column);
        Assert.Equal("Foo.Bar".Length, occurrence.Token.Span.Length);
    }

    [Fact]
    public void FuncTypeSignature_YieldsEveryLeafType()
    {
        Assert.Equal(["A/0", "B/0", "C/0"], Names("(define (f [g : (A B -> C)]) g)"));
    }

    [Fact]
    public void NamedType_CarriesTypeArgumentArity()
    {
        Assert.Equal(
            ["Dictionary/2", "String/0", "Int/0"],
            Names("(define (f [d : (Dictionary String Int)]) d)")
        );
    }

    [Fact]
    public void TupleType_SkipsStarSeparators()
    {
        Assert.Equal(
            ["Int/0", "String/0", "Bool/0"],
            Names("(define (f [t : (Int * String * Bool)]) t)")
        );
    }

    [Fact]
    public void NullableSuffix_IsStrippedFromTheNameButNotTheToken()
    {
        var occurrence = Assert.Single(Scan("(define (f [x : Foo.Bar?]) x)").TypeNames);

        Assert.Equal("Foo.Bar", occurrence.Name);
        Assert.Equal("Foo.Bar?", occurrence.Token.Text);
    }

    [Fact]
    public void DelegateType_IsSkippedEntirely()
    {
        Assert.Empty(Scan("(define (f [g : (delegate System.Func<int,int>)]) g)").TypeNames);
    }

    [Fact]
    public void ImportClrMemberPath_IsRecordedApartFromItsSignature()
    {
        var src = """
            (import-clr
              [sb-append System.Text.StringBuilder.Append
                :instance : (System.Text.StringBuilder String -> System.Text.StringBuilder)])
            """;
        var scan = Scan(src);

        // The path is not a type *position* — only the signature is — so TypeNames is unchanged.
        Assert.Equal(
            ["System.Text.StringBuilder", "String", "System.Text.StringBuilder"],
            scan.TypeNames.Select(t => t.Name)
        );

        var member = Assert.Single(scan.ImportMembers);
        Assert.Equal("System.Text.StringBuilder", member.TypeName);
        // The token is the whole path, so the qualifier still starts at its column.
        Assert.Equal("System.Text.StringBuilder.Append", member.Token.Text);
    }

    [Fact]
    public void ImportClrKeywordsAndAssemblyHints_AreNotTypes()
    {
        var src = """
            (import-clr
              [len System.String.Length :instance-property : (System.String -> Int) :from "System.Runtime"]
              [make Some.Ns.Box/Create ^a : where (^a notnull) : (^a -> Some.Ns.Box)])
            """;
        var scan = Scan(src);

        Assert.Equal(["System.String", "Int", "Some.Ns.Box"], scan.TypeNames.Select(t => t.Name));
        // Both split rules in one assertion: the last '.' for the property, the '/' for the
        // static — whose type half keeps its own dots.
        Assert.Equal(["System.String", "Some.Ns.Box"], scan.ImportMembers.Select(m => m.TypeName));
    }

    [Fact]
    public void ImportClrMemberPathWithoutASeparator_IsNotRecorded()
    {
        Assert.Empty(Scan("(import-clr [x Foo])").ImportMembers);
    }

    [Fact]
    public void ImportClrNamespaces_AreCollectedWithTheirTokens()
    {
        var src = "(import-clr System.Text [x A/B] System.IO)";
        var scan = Scan(src);

        Assert.Equal(["System.Text", "System.IO"], scan.ClrNamespaces.Select(t => t.Text));
        Assert.Equal(13, scan.ClrNamespaces[0].Span.Column);
        // A bare namespace atom is never mistaken for a member path, dots notwithstanding.
        Assert.Equal(["A"], scan.ImportMembers.Select(m => m.TypeName));
    }

    [Fact]
    public void DefineClassBaseRun_AreTypeNames_AndTheDeclaredNameIsNot()
    {
        Assert.Equal(
            ["Ns.Animal/0", "Ns.IPet/0", "Int/0"],
            Names("(define-class #:open Dog : Ns.Animal Ns.IPet [age : Int])")
        );
    }

    [Fact]
    public void DefineInterfaceBaseRun_AreTypeNames()
    {
        Assert.Equal(
            ["Ns.IFoo/0", "Ns.IBar/0"],
            Names("(define-interface IBaz : Ns.IFoo Ns.IBar)")
        );
    }

    [Fact]
    public void ObjectExpression_BaseAndInterfaceGroup_AreTypeNames()
    {
        Assert.Equal(
            ["Ns.Base/0", "Ns.IFoo/0", "Ns.IBar/0"],
            Names("(object : Ns.Base (Ns.IFoo Ns.IBar) (define (m) 1))")
        );
    }

    [Fact]
    public void WithHandlers_ExceptionTypeIsATypeName()
    {
        Assert.Equal(["Ns.MyError/0"], Names("(with-handlers ([Ns.MyError e] 0) (f))"));
    }

    [Fact]
    public void NewAndTypeof_ArgumentsAreTypes_ButConstructorArgumentsAreNot()
    {
        Assert.Equal(["Ns.Box/0"], Names("(new Ns.Box other.thing 1)"));
        Assert.Equal(["Ns.List/1", "Int/0"], Names("(typeof (Ns.List Int))"));
    }

    [Fact]
    public void TypeVarsKeywordFlagsAndMemberPaths_AreExcluded()
    {
        Assert.Empty(Scan("(define (f [x : ^a]) x)").TypeNames);
        Assert.Empty(Scan("(define-class #:open C)").TypeNames);
        Assert.Empty(Scan("(define (f) (Some.Ns.Box/Create 1))").TypeNames);
    }

    [Fact]
    public void QuotedDatum_IsSkipped()
    {
        Assert.Empty(Scan("(define xs '(a : Foo.Bar))").TypeNames);
    }

    [Fact]
    public void DefineSyntaxTemplate_IsSkipped()
    {
        var src = """
            (define-syntax wrap
              (syntax-rules ()
                [(_ e) (let ([tmp : Foo.Bar e]) tmp)]))
            """;

        Assert.Empty(Scan(src).TypeNames);
    }

    [Fact]
    public void UnbalancedSource_Terminates()
    {
        Assert.Equal(["Foo.Bar"], Scan("(define (f [x : Foo.Bar]").TypeNames.Select(t => t.Name));
    }

    /// <summary>1-based (line, column) of <paramref name="token" /> in
    ///     <paramref name="source" /> — the same convention <c>Token.Span</c> uses.</summary>
    private static (int Line, int Col) LspTestSessionLocate(string source, string token)
    {
        return TestFixtures.LspTestSession.Locate(source, token);
    }
}
