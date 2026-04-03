using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests;

public class AttributeTests
{
    // --- Lexer ---

    private static List<Token> Lex(string source)
    {
        var diag = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diag);
        var tokens = lexer.Tokenize();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
        return tokens;
    }

    [Fact]
    public void Lexer_AtSign_TokenizesAsSymbol()
    {
        var tokens = Lex("@");
        Assert.Equal(TokenKind.Symbol, tokens[0].Kind);
        Assert.Equal("@", tokens[0].Text);
    }

    [Fact]
    public void Lexer_AtInList_TokenizesCorrectly()
    {
        var tokens = Lex("(@ Serializable)");
        Assert.Equal(TokenKind.LParen, tokens[0].Kind);
        Assert.Equal(TokenKind.Symbol, tokens[1].Kind);
        Assert.Equal("@", tokens[1].Text);
        Assert.Equal(TokenKind.Symbol, tokens[2].Kind);
        Assert.Equal("Serializable", tokens[2].Text);
        Assert.Equal(TokenKind.RParen, tokens[3].Kind);
    }

    // --- AST Builder ---

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

    private static AstNode.Program BuildWithErrors(string source, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        var lexer = new Lexer(source, "test.zs", diagnostics);
        var tokens = lexer.Tokenize();
        var parser = new SExprParser(tokens, diagnostics);
        var sexprs = parser.ParseAll();
        var builder = new AstBuilder(diagnostics);
        return builder.BuildProgram(sexprs);
    }

    [Fact]
    public void AstBuilder_SimpleAttribute_OnRecord()
    {
        var prog = Build("(@ Serializable)\n(record Point [x : Float] [y : Float])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.Equal("Point", rec.RecordName);
        Assert.NotNull(rec.Attributes);
        Assert.Single(rec.Attributes);
        Assert.Equal("Serializable", rec.Attributes[0].Name);
        Assert.Empty(rec.Attributes[0].PositionalArgs);
        Assert.Empty(rec.Attributes[0].NamedArgs);
    }

    [Fact]
    public void AstBuilder_AttributeWithPositionalArg()
    {
        var prog = Build("(@ Obsolete \"Use new-fn instead\")\n(define (old-fn [x : Int]) : Int x)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.NotNull(def.Attributes);
        Assert.Single(def.Attributes);
        Assert.Equal("Obsolete", def.Attributes[0].Name);
        Assert.Single(def.Attributes[0].PositionalArgs);
        Assert.Equal("Use new-fn instead", def.Attributes[0].PositionalArgs[0]);
    }

    [Fact]
    public void AstBuilder_AttributeWithNamedArgs()
    {
        var prog = Build(
            "(@ DllImport \"kernel32.dll\" [EntryPoint \"GetTickCount\"] [SetLastError #t])\n(define (get-ticks) : Int 0)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.NotNull(def.Attributes);
        Assert.Single(def.Attributes);
        var attr = def.Attributes[0];
        Assert.Equal("DllImport", attr.Name);
        Assert.Single(attr.PositionalArgs);
        Assert.Equal("kernel32.dll", attr.PositionalArgs[0]);
        Assert.Equal(2, attr.NamedArgs.Count);
        Assert.Equal("EntryPoint", attr.NamedArgs[0].Name);
        Assert.Equal("GetTickCount", attr.NamedArgs[0].Value);
        Assert.Equal("SetLastError", attr.NamedArgs[1].Name);
        Assert.Equal(true, attr.NamedArgs[1].Value);
    }

    [Fact]
    public void AstBuilder_MultipleAttributes_OnOneTarget()
    {
        var prog = Build("(@ Serializable)\n(@ Obsolete \"deprecated\")\n(record OldPoint [x : Float])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.NotNull(rec.Attributes);
        Assert.Equal(2, rec.Attributes.Count);
        Assert.Equal("Serializable", rec.Attributes[0].Name);
        Assert.Equal("Obsolete", rec.Attributes[1].Name);
    }

    [Fact]
    public void AstBuilder_AttributeOnDefineValue()
    {
        var prog = Build("(@ Obsolete \"old\")\n(define x 42)");
        var dv = Assert.IsType<AstNode.DefineValue>(prog.TopLevelForms[0]);
        Assert.NotNull(dv.Attributes);
        Assert.Single(dv.Attributes);
        Assert.Equal("Obsolete", dv.Attributes[0].Name);
    }

    [Fact]
    public void AstBuilder_AttributeOnUnion()
    {
        var prog = Build("(@ Serializable)\n(union Shape (Circle [r : Float]) (Rect [w : Float] [h : Float]))");
        var union = Assert.IsType<AstNode.UnionDecl>(prog.TopLevelForms[0]);
        Assert.NotNull(union.Attributes);
        Assert.Single(union.Attributes);
        Assert.Equal("Serializable", union.Attributes[0].Name);
    }

    [Fact]
    public void AstBuilder_FieldAttribute()
    {
        var prog = Build("(record Point [(@ JsonProperty \"x_coord\") x : Float] [y : Float])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.Equal(2, rec.Fields.Count);

        Assert.NotNull(rec.Fields[0].Attributes);
        Assert.Single(rec.Fields[0].Attributes!);
        Assert.Equal("JsonProperty", rec.Fields[0].Attributes![0].Name);
        Assert.Equal("x_coord", rec.Fields[0].Attributes![0].PositionalArgs[0]);
        Assert.Equal("x", rec.Fields[0].Name);

        Assert.Null(rec.Fields[1].Attributes);
        Assert.Equal("y", rec.Fields[1].Name);
    }

    [Fact]
    public void AstBuilder_ParamAttribute()
    {
        var prog = Build("(define (handler [(@ FromBody) request : Int]) : Int request)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.Single(def.Params);
        Assert.NotNull(def.Params[0].Attributes);
        Assert.Single(def.Params[0].Attributes!);
        Assert.Equal("FromBody", def.Params[0].Attributes![0].Name);
        Assert.Equal("request", def.Params[0].Name);
    }

    [Fact]
    public void AstBuilder_NoAttributes_ReturnsNull()
    {
        var prog = Build("(record Point [x : Float])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.Null(rec.Attributes);
        Assert.Null(rec.Fields[0].Attributes);
    }

    [Fact]
    public void AstBuilder_TrailingAttribute_Error()
    {
        var prog = BuildWithErrors("(@ Serializable)", out var diag);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("no target"));
    }

    // --- IR Lowering ---

    [Fact]
    public void IrLowering_Attributes_SurviveLowering_FuncDef()
    {
        var attrs = new List<AttributeDecl>
        {
            new("Obsolete", ["old msg"], [], SourceSpan.None)
        };
        var define = new AstNode.Define(
            "f", [new Param("x", ZType.Int, SourceSpan.None)],
            ZType.Int, new AstNode.Name("x", SourceSpan.None), SourceSpan.None, attrs);
        define.ResolvedType = new ZType.ZFuncType([ZType.Int], ZType.Int);

        var lowering = new IrLowering(new DiagnosticBag());
        var result = lowering.Lower(define);
        var func = Assert.IsType<IrNode.FuncDef>(result);
        Assert.NotNull(func.Attributes);
        Assert.Single(func.Attributes!);
        Assert.Equal("Obsolete", func.Attributes![0].Name);
    }

    [Fact]
    public void IrLowering_Attributes_SurviveLowering_RecordDecl()
    {
        var attrs = new List<AttributeDecl>
        {
            new("Serializable", [], [], SourceSpan.None)
        };
        var fieldAttrs = new List<AttributeDecl>
        {
            new("JsonProperty", ["x_coord"], [], SourceSpan.None)
        };
        var record = new AstNode.RecordDecl(
            "Point", [],
            [new FieldDecl("x", ZType.Float, SourceSpan.None, fieldAttrs)],
            SourceSpan.None, attrs);

        var lowering = new IrLowering(new DiagnosticBag());
        var result = lowering.Lower(record);
        var rec = Assert.IsType<IrNode.RecordDecl>(result);
        Assert.NotNull(rec.Attributes);
        Assert.Single(rec.Attributes!);
        Assert.Equal("Serializable", rec.Attributes![0].Name);
        Assert.NotNull(rec.Fields[0].Attributes);
        Assert.Equal("JsonProperty", rec.Fields[0].Attributes![0].Name);
    }

    [Fact]
    public void IrLowering_Attributes_SurviveLowering_UnionDecl()
    {
        var attrs = new List<AttributeDecl>
        {
            new("Serializable", [], [], SourceSpan.None)
        };
        var union = new AstNode.UnionDecl(
            "Shape", [],
            [new UnionCase("Circle", [new FieldDecl("r", ZType.Float, SourceSpan.None)], SourceSpan.None)],
            SourceSpan.None, attrs);

        var lowering = new IrLowering(new DiagnosticBag());
        var result = lowering.Lower(union);
        var u = Assert.IsType<IrNode.UnionDecl>(result);
        Assert.NotNull(u.Attributes);
        Assert.Single(u.Attributes!);
        Assert.Equal("Serializable", u.Attributes![0].Name);
    }

    [Fact]
    public void IrLowering_ParamAttributes_SurviveLowering()
    {
        var paramAttrs = new List<AttributeDecl>
        {
            new("FromBody", [], [], SourceSpan.None)
        };
        var define = new AstNode.Define(
            "handler",
            [new Param("request", ZType.Int, SourceSpan.None, paramAttrs)],
            ZType.Int, new AstNode.Name("request", SourceSpan.None), SourceSpan.None);
        define.ResolvedType = new ZType.ZFuncType([ZType.Int], ZType.Int);

        var lowering = new IrLowering(new DiagnosticBag());
        var result = lowering.Lower(define);
        var func = Assert.IsType<IrNode.FuncDef>(result);
        Assert.NotNull(func.Params[0].Attributes);
        Assert.Single(func.Params[0].Attributes!);
        Assert.Equal("FromBody", func.Params[0].Attributes![0].Name);
    }

    // --- C# Emitter (end-to-end) ---

    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions
            { OutputMode = OutputMode.CSharp, AllowsImplicitModuleName = true });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Diagnostics));
        var csResult = (CompilationResult.CSharpOutputResult)result;
        return csResult.CsOutput;
    }

    [Fact]
    public void Emitter_SimpleAttribute_OnRecord()
    {
        var cs = Compile("(@ Serializable)\n(record Point [x : Float] [y : Float])");
        Assert.Contains("[Serializable]", cs);
        Assert.Contains("public sealed record Point(float X, float Y);", cs);
    }

    [Fact]
    public void Emitter_AttributeWithPositionalArg()
    {
        var cs = Compile("(module test)\n(@ Obsolete \"Use new-fn instead\")\n(define (old-fn [x : Int]) : Int x)");
        Assert.Contains("[Obsolete(\"Use new-fn instead\")]", cs);
        Assert.Contains("public static int OldFn(int x)", cs);
    }

    [Fact]
    public void Emitter_AttributeWithNamedArgs()
    {
        var cs = Compile(
            "(module test)\n(@ DllImport \"kernel32.dll\" [EntryPoint \"GetTickCount\"])\n(define (get-ticks [x : Int]) : Int 0)");
        Assert.Contains("[DllImport(\"kernel32.dll\", EntryPoint = \"GetTickCount\")]", cs);
    }

    [Fact]
    public void Emitter_MultipleAttributes()
    {
        var cs = Compile("(@ Serializable)\n(@ Obsolete \"deprecated\")\n(record OldPoint [x : Float])");
        Assert.Contains("[Serializable]", cs);
        Assert.Contains("[Obsolete(\"deprecated\")]", cs);
        Assert.Contains("public sealed record OldPoint(float X);", cs);
    }

    [Fact]
    public void Emitter_FieldAttribute_PropertyTarget()
    {
        var cs = Compile("(record Point [(@ JsonProperty \"x_coord\") x : Float] [y : Float])");
        Assert.Contains("[property: JsonProperty(\"x_coord\")]", cs);
    }

    [Fact]
    public void Emitter_ParamAttribute()
    {
        var cs = Compile("(module test)\n(define (handler [(@ FromBody) request : Int]) : Int request)");
        Assert.Contains("[FromBody] int request", cs);
    }

    [Fact]
    public void Emitter_AttributeOnUnion()
    {
        var cs = Compile("(@ Serializable)\n(union Shape (Circle [r : Float]) (Rect [w : Float] [h : Float]))");
        Assert.Contains("[Serializable]", cs);
        Assert.Contains("public abstract record Shape;", cs);
    }

    [Fact]
    public void Emitter_AttributeWithBoolNamedArg()
    {
        var cs = Compile(
            "(module test)\n(@ DllImport \"user32.dll\" [SetLastError #t])\n(define (msg-box [x : Int]) : Int 0)");
        Assert.Contains("SetLastError = true", cs);
    }

    [Fact]
    public void Emitter_AttributeWithIntPositionalArg()
    {
        var cs = Compile("(@ StructLayout 0)\n(record Data [x : Int])");
        Assert.Contains("[StructLayout(0)]", cs);
    }

    [Fact]
    public void Emitter_NoAttributes_NoSquareBrackets()
    {
        var cs = Compile("(record Point [x : Float])");
        // The output should not have any stray attribute brackets
        Assert.DoesNotContain("[Serializable]", cs);
        Assert.DoesNotContain("[property:", cs);
    }

    // --- import-clr namespace imports ---

    [Fact]
    public void AstBuilder_ImportClr_BareNamespace()
    {
        var prog = Build("(import-clr System.Text.Json.Serialization)");
        var importClr = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Empty(importClr.Imports);
        Assert.Single(importClr.Namespaces);
        Assert.Equal("System.Text.Json.Serialization", importClr.Namespaces[0]);
    }

    [Fact]
    public void AstBuilder_ImportClr_MixedForm()
    {
        var prog = Build("(import-clr System.Text.Json.Serialization [writeln System.Console/WriteLine])");
        var importClr = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Single(importClr.Imports);
        Assert.Equal("writeln", importClr.Imports[0].Alias);
        Assert.Single(importClr.Namespaces);
        Assert.Equal("System.Text.Json.Serialization", importClr.Namespaces[0]);
    }

    [Fact]
    public void Emitter_ImportClr_UsingDirective()
    {
        var cs = Compile("(module test)\n(import-clr System.Text.Json.Serialization)\n(define x 42)");
        Assert.Contains("using System.Text.Json.Serialization;", cs);
        // using should appear before namespace
        var usingIdx = cs.IndexOf("using System.Text.Json.Serialization;");
        var nsIdx = cs.IndexOf("namespace ");
        Assert.True(usingIdx < nsIdx, "using directive should appear before namespace");
    }

    [Fact]
    public void Emitter_ImportClr_MultipleNamespaces()
    {
        var cs = Compile("(module test)\n(import-clr System.Text.Json System.Text.Json.Serialization)\n(define x 42)");
        Assert.Contains("using System.Text.Json;", cs);
        Assert.Contains("using System.Text.Json.Serialization;", cs);
    }

    [Fact]
    public void Emitter_ImportClr_NoNamespace_NoUsing()
    {
        var cs = Compile("(module test)\n(define x 42)");
        Assert.DoesNotContain("using ", cs);
    }
}
