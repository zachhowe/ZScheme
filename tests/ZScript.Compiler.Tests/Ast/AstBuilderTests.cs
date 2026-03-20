namespace ZScript.Compiler.Tests.Ast;

using ZScript.Compiler.Ast;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;
using Xunit;

public class AstBuilderTests
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

    [Fact]
    public void IntLiteral()
    {
        var prog = Build("42");
        var lit = Assert.IsType<AstNode.IntLit>(prog.TopLevelForms[0]);
        Assert.Equal(42, lit.Value);
    }

    [Fact]
    public void FloatLiteral()
    {
        var prog = Build("3.14");
        var lit = Assert.IsType<AstNode.FloatLit>(prog.TopLevelForms[0]);
        Assert.Equal(3.14f, lit.Value);
    }

    [Fact]
    public void BoolLiteral()
    {
        var prog = Build("true");
        var lit = Assert.IsType<AstNode.BoolLit>(prog.TopLevelForms[0]);
        Assert.True(lit.Value);
    }

    [Fact]
    public void StringLiteral()
    {
        var prog = Build("\"hello\"");
        var lit = Assert.IsType<AstNode.StringLit>(prog.TopLevelForms[0]);
        Assert.Equal("hello", lit.Value);
    }

    [Fact]
    public void NameReference()
    {
        var prog = Build("x");
        var name = Assert.IsType<AstNode.Name>(prog.TopLevelForms[0]);
        Assert.Equal("x", name.Value);
    }

    [Fact]
    public void FunctionApplication()
    {
        var prog = Build("(+ 1 2)");
        var app = Assert.IsType<AstNode.Apply>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.Name>(app.Function);
        Assert.Equal(2, app.Args.Count);
    }

    [Fact]
    public void DefineFunction()
    {
        var prog = Build("(define (add [x : Int] [y : Int]) : Int (+ x y))");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.Equal("add", def.FnName);
        Assert.Equal(2, def.Params.Count);
        Assert.Equal("x", def.Params[0].Name);
        Assert.Equal("y", def.Params[1].Name);
        Assert.Equal(ZType.Int, def.Params[0].TypeAnnotation);
        Assert.Equal(ZType.Int, def.Params[1].TypeAnnotation);
        Assert.Equal(ZType.Int, def.ReturnTypeAnnotation);
    }

    [Fact]
    public void DefineValue()
    {
        var prog = Build("(define x 42)");
        var def = Assert.IsType<AstNode.DefineValue>(prog.TopLevelForms[0]);
        Assert.Equal("x", def.VarName);
    }

    [Fact]
    public void LetBinding()
    {
        var prog = Build("(let [x 5] (+ x 1))");
        var let = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("x", let.VarName);
        Assert.IsType<AstNode.IntLit>(let.Value);
        Assert.IsType<AstNode.Apply>(let.Body);
    }

    [Fact]
    public void IfExpression()
    {
        var prog = Build("(if true 1 2)");
        var @if = Assert.IsType<AstNode.If>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.BoolLit>(@if.Condition);
        Assert.IsType<AstNode.IntLit>(@if.Then);
        Assert.IsType<AstNode.IntLit>(@if.Else);
    }

    [Fact]
    public void Lambda()
    {
        var prog = Build("(fn [x y] (+ x y))");
        var lam = Assert.IsType<AstNode.Lambda>(prog.TopLevelForms[0]);
        Assert.Equal(2, lam.Params.Count);
        Assert.Equal("x", lam.Params[0].Name);
        Assert.Equal("y", lam.Params[1].Name);
    }

    [Fact]
    public void RecordDecl()
    {
        var prog = Build("(record Point [x : Float] [y : Float])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.Equal("Point", rec.RecordName);
        Assert.Empty(rec.TypeParams);
        Assert.Equal(2, rec.Fields.Count);
    }

    [Fact]
    public void GenericRecordDecl()
    {
        var prog = Build("(record (Pair a b) [fst : a] [snd : b])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.Equal("Pair", rec.RecordName);
        Assert.Equal(2, rec.TypeParams.Count);
    }

    [Fact]
    public void UnionDecl()
    {
        var prog = Build("(union Shape (Circle [radius : Float]) (Rect [w : Float] [h : Float]))");
        var u = Assert.IsType<AstNode.UnionDecl>(prog.TopLevelForms[0]);
        Assert.Equal("Shape", u.UnionName);
        Assert.Equal(2, u.Cases.Count);
        Assert.Equal("Circle", u.Cases[0].Name);
        Assert.Equal("Rect", u.Cases[1].Name);
    }

    [Fact]
    public void MatchExpression()
    {
        var source = @"(match x
  [1 ""one""]
  [2 ""two""]
  [_ ""other""])";
        var prog = Build(source);
        var m = Assert.IsType<AstNode.Match>(prog.TopLevelForms[0]);
        Assert.Equal(3, m.Arms.Count);
    }

    [Fact]
    public void PipeExpression()
    {
        var prog = Build("(|> x (f 1) (g 2))");
        var pipe = Assert.IsType<AstNode.Pipe>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.Name>(pipe.Initial);
        Assert.Equal(2, pipe.Steps.Count);
    }

    [Fact]
    public void PartialApplication()
    {
        var prog = Build("(partial add 5)");
        var part = Assert.IsType<AstNode.Partial>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.Name>(part.Function);
        Assert.Single(part.Args);
    }

    [Fact]
    public void ImportClr()
    {
        var prog = Build("(import-clr [sqrt System.Math/Sqrt] [console System.Console/WriteLine])");
        var imp = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Equal(2, imp.Imports.Count);
        Assert.Equal("sqrt", imp.Imports[0].Alias);
        Assert.Equal("System.Math/Sqrt", imp.Imports[0].QualifiedName);
    }

    [Fact]
    public void ModuleDecl()
    {
        var prog = Build("(module math/vector)");
        var mod = Assert.IsType<AstNode.ModuleDecl>(prog.TopLevelForms[0]);
        Assert.Equal("math/vector", mod.ModuleName);
    }

    [Fact]
    public void ImportDecl()
    {
        var prog = Build("(import geometry)");
        var imp = Assert.IsType<AstNode.Import>(prog.TopLevelForms[0]);
        Assert.Equal("geometry", imp.ModuleName);
    }

    [Fact]
    public void ListExpression()
    {
        var prog = Build("(list 1 2 3)");
        var list = Assert.IsType<AstNode.ListExpr>(prog.TopLevelForms[0]);
        Assert.Equal(3, list.Elements.Count);
    }

    [Fact]
    public void VectorExpression()
    {
        var prog = Build("(vector 1 2 3)");
        var vec = Assert.IsType<AstNode.VectorExpr>(prog.TopLevelForms[0]);
        Assert.Equal(3, vec.Elements.Count);
    }

    [Fact]
    public void MapExpression()
    {
        var prog = Build("(map-of (\"a\" 1) (\"b\" 2))");
        var map = Assert.IsType<AstNode.MapExpr>(prog.TopLevelForms[0]);
        Assert.Equal(2, map.Entries.Count);
    }

    [Fact]
    public void TryAndPropagate()
    {
        var prog = Build("(try (? x))");
        var t = Assert.IsType<AstNode.Try>(prog.TopLevelForms[0]);
        var prop = Assert.IsType<AstNode.Propagate>(t.Body);
        Assert.IsType<AstNode.Name>(prop.Expr);
    }

    [Fact]
    public void EmptyList_IsUnit()
    {
        var prog = Build("()");
        Assert.IsType<AstNode.UnitLit>(prog.TopLevelForms[0]);
    }

    [Fact]
    public void CompleteFactorial()
    {
        var source = @"(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))";
        var prog = Build(source);
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.Equal("factorial", def.FnName);
        Assert.Equal(2, def.Params.Count);
        Assert.IsType<AstNode.If>(def.Body);
    }

    [Fact]
    public void ClrNew_NoArgs()
    {
        var prog = Build("(new System.Object)");
        var clrNew = Assert.IsType<AstNode.ClrNew>(prog.TopLevelForms[0]);
        Assert.Equal("System.Object", clrNew.TypeName);
        Assert.Empty(clrNew.Args);
    }

    [Fact]
    public void ClrNew_WithArgs()
    {
        var prog = Build("(new System.Collections.ArrayList 10)");
        var clrNew = Assert.IsType<AstNode.ClrNew>(prog.TopLevelForms[0]);
        Assert.Equal("System.Collections.ArrayList", clrNew.TypeName);
        Assert.Single(clrNew.Args);
        Assert.IsType<AstNode.IntLit>(clrNew.Args[0]);
    }

    [Fact]
    public void ClrNew_MultipleArgs()
    {
        var prog = Build("(new System.Text.StringBuilder \"hello\" 256)");
        var clrNew = Assert.IsType<AstNode.ClrNew>(prog.TopLevelForms[0]);
        Assert.Equal("System.Text.StringBuilder", clrNew.TypeName);
        Assert.Equal(2, clrNew.Args.Count);
        Assert.IsType<AstNode.StringLit>(clrNew.Args[0]);
        Assert.IsType<AstNode.IntLit>(clrNew.Args[1]);
    }

    [Fact]
    public void ClrNew_NestedExprArgs()
    {
        var prog = Build("(new System.Collections.ArrayList (+ 1 2))");
        var clrNew = Assert.IsType<AstNode.ClrNew>(prog.TopLevelForms[0]);
        Assert.Equal("System.Collections.ArrayList", clrNew.TypeName);
        Assert.Single(clrNew.Args);
        Assert.IsType<AstNode.Apply>(clrNew.Args[0]);
    }

    [Fact]
    public void ObjectExpr_SingleInterface()
    {
        var source = @"(object IComparer
  (Compare [x : Int] [y : Int] : Int
    (- x y)))";
        var prog = Build(source);
        var obj = Assert.IsType<AstNode.ObjectExpr>(prog.TopLevelForms[0]);
        Assert.Single(obj.InterfaceNames);
        Assert.Equal("IComparer", obj.InterfaceNames[0]);
        Assert.Single(obj.Methods);
        Assert.Equal("Compare", obj.Methods[0].Name);
        Assert.Equal(2, obj.Methods[0].Params.Count);
        Assert.Equal("x", obj.Methods[0].Params[0].Name);
        Assert.Equal(ZType.Int, obj.Methods[0].Params[0].TypeAnnotation);
        Assert.Equal(ZType.Int, obj.Methods[0].ReturnTypeAnnotation);
    }

    [Fact]
    public void ObjectExpr_MultipleInterfaces()
    {
        var source = @"(object (IFoo IBar)
  (DoFoo : Int 42)
  (DoBar [x : Int] : Int x))";
        var prog = Build(source);
        var obj = Assert.IsType<AstNode.ObjectExpr>(prog.TopLevelForms[0]);
        Assert.Equal(2, obj.InterfaceNames.Count);
        Assert.Equal("IFoo", obj.InterfaceNames[0]);
        Assert.Equal("IBar", obj.InterfaceNames[1]);
        Assert.Equal(2, obj.Methods.Count);
        Assert.Equal("DoFoo", obj.Methods[0].Name);
        Assert.Empty(obj.Methods[0].Params);
        Assert.Equal("DoBar", obj.Methods[1].Name);
        Assert.Single(obj.Methods[1].Params);
    }
}
