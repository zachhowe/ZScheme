using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Generics;

public class GenericConstraintTests
{
    private static AstNode.Program Build(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diag);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diag);
        var program = builder.BuildProgram(sexprs);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return program;
    }

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

    // --- Define constraint parsing ---

    [Fact]
    public void ParseDefine_SingleConstraint_NotNull()
    {
        var prog = Build("(define (f [x : ^a]) : ^a :where (^a notnull) x)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.NotNull(def.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.NotNull, def.TypeParamConstraints["^a"]);
    }

    [Fact]
    public void ParseDefine_SingleConstraint_Struct()
    {
        var prog = Build("(define (f [x : ^a]) : ^a :where (^a struct) x)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.NotNull(def.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.Struct, def.TypeParamConstraints["^a"]);
    }

    [Fact]
    public void ParseDefine_SingleConstraint_Class()
    {
        var prog = Build("(define (f [x : ^a]) : ^a :where (^a class) x)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.NotNull(def.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.Class, def.TypeParamConstraints["^a"]);
    }

    [Fact]
    public void ParseDefine_SingleConstraint_New()
    {
        var prog = Build("(define (f [x : ^a]) : ^a :where (^a new) x)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.NotNull(def.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.New, def.TypeParamConstraints["^a"]);
    }

    [Fact]
    public void ParseDefine_SingleConstraint_Unmanaged()
    {
        var prog = Build("(define (f [x : ^a]) : ^a :where (^a unmanaged) x)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.NotNull(def.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.Unmanaged, def.TypeParamConstraints["^a"]);
    }

    [Fact]
    public void ParseDefine_SingleConstraint_Default()
    {
        var prog = Build("(define (f [x : ^a]) : ^a :where (^a default) x)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.NotNull(def.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.Default, def.TypeParamConstraints["^a"]);
    }

    [Fact]
    public void ParseDefine_MultipleConstraints()
    {
        var prog = Build("(define (f [x : ^a] [y : ^b]) : ^a :where ((^a struct) (^b class)) x)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.NotNull(def.TypeParamConstraints);
        Assert.Equal(2, def.TypeParamConstraints.Count);
        Assert.Equal(GenericConstraintKind.Struct, def.TypeParamConstraints["^a"]);
        Assert.Equal(GenericConstraintKind.Class, def.TypeParamConstraints["^b"]);
    }

    [Fact]
    public void ParseDefine_CombinedFlags()
    {
        var prog = Build("(define (f [x : ^a]) : ^a :where (^a class new) x)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.NotNull(def.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.Class | GenericConstraintKind.New, def.TypeParamConstraints["^a"]);
    }

    [Fact]
    public void ParseDefine_NoConstraints_HasNullConstraints()
    {
        var prog = Build("(define (f [x : ^a]) : ^a x)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.Null(def.TypeParamConstraints);
    }

    [Fact]
    public void ParseDefine_UnknownConstraint_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(define (f [x : ^a]) : ^a :where (^a bogus) x)");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Unknown constraint"));
    }

    // --- DefineAsync constraint parsing ---

    [Fact]
    public void ParseDefineAsync_WithConstraint()
    {
        var prog = Build("(define-async (f [x : ^a]) : ^a :where (^a notnull) x)");
        var def = Assert.IsType<AstNode.DefineAsync>(prog.TopLevelForms[0]);
        Assert.NotNull(def.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.NotNull, def.TypeParamConstraints["^a"]);
    }

    [Fact]
    public void ParseDefineAsync_NoConstraints_HasNullConstraints()
    {
        var prog = Build("(define-async (f [x : Int]) : Int x)");
        var def = Assert.IsType<AstNode.DefineAsync>(prog.TopLevelForms[0]);
        Assert.Null(def.TypeParamConstraints);
    }

    // --- Record constraint parsing ---

    [Fact]
    public void ParseRecord_WithConstraint()
    {
        var prog = Build("(define-record (Box ^a) :where (^a notnull) [value : ^a])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.NotNull(rec.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.NotNull, rec.TypeParamConstraints["^a"]);
    }

    [Fact]
    public void ParseRecord_NoConstraints()
    {
        var prog = Build("(define-record (Pair a b) [fst : a] [snd : b])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.Null(rec.TypeParamConstraints);
    }

    // --- Union constraint parsing ---

    [Fact]
    public void ParseUnion_WithConstraint()
    {
        var prog = Build("(define-union (Maybe ^a) :where (^a notnull) (Just [value : ^a]) (Nothing))");
        var u = Assert.IsType<AstNode.UnionDecl>(prog.TopLevelForms[0]);
        Assert.NotNull(u.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.NotNull, u.TypeParamConstraints["^a"]);
    }

    [Fact]
    public void ParseUnion_NoConstraints()
    {
        var prog = Build("(define-union (Maybe a) (Just [value : a]) (Nothing))");
        var u = Assert.IsType<AstNode.UnionDecl>(prog.TopLevelForms[0]);
        Assert.Null(u.TypeParamConstraints);
    }

    // --- Import-CLR constraint parsing ---

    [Fact]
    public void ParseImportClr_WithConstraint()
    {
        var source = @"(import-clr
  [my-fn System.String/Concat ^a
    :where (^a notnull)
    : (^a -> String)])";
        var prog = Build(source);
        var importClr = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Single(importClr.Imports);
        var import = importClr.Imports[0];
        Assert.NotNull(import.TypeParamConstraints);
        Assert.Equal(GenericConstraintKind.NotNull, import.TypeParamConstraints["^a"]);
    }
}
