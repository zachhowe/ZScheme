using Xunit;
using ZScheme.Compiler.Ast;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Syntax;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Ast;

public class AstBuilderTests
{
    private static AstNode.Program Build(string source)
    {
        var (program, diag) = BuildWithDiagnostics(source);
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

    private static void AssertHasError(DiagnosticBag diag, string expectedSubstring)
    {
        Assert.True(diag.HasErrors, "Expected errors but none were reported");
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains(expectedSubstring));
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
        var prog = Build("#t");
        var lit = Assert.IsType<AstNode.BoolLit>(prog.TopLevelForms[0]);
        Assert.True(lit.Value);
    }

    [Fact]
    public void BoolLiteralFalse()
    {
        var prog = Build("#f");
        var lit = Assert.IsType<AstNode.BoolLit>(prog.TopLevelForms[0]);
        Assert.False(lit.Value);
    }

    [Fact]
    public void NullLiteral()
    {
        var prog = Build("null");
        Assert.IsType<AstNode.NullLit>(prog.TopLevelForms[0]);
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
    public void LetStarBinding()
    {
        var prog = Build("(let* ([x 1] [y (+ x 1)]) (+ x y))");
        // Should desugar to nested lets: (let [x 1] (let [y (+ x 1)] (+ x y)))
        var outerLet = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("x", outerLet.VarName);
        Assert.IsType<AstNode.IntLit>(outerLet.Value);
        var innerLet = Assert.IsType<AstNode.Let>(outerLet.Body);
        Assert.Equal("y", innerLet.VarName);
        Assert.IsType<AstNode.Apply>(innerLet.Value);
        Assert.IsType<AstNode.Apply>(innerLet.Body);
    }

    [Fact]
    public void LetStarSingleBinding()
    {
        var prog = Build("(let* ([x 5]) (+ x 1))");
        var let = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("x", let.VarName);
        Assert.IsType<AstNode.IntLit>(let.Value);
        Assert.IsType<AstNode.Apply>(let.Body);
    }

    [Fact]
    public void LetStarEmptyBindings()
    {
        var prog = Build("(let* () 42)");
        // Zero bindings → just the body
        Assert.IsType<AstNode.IntLit>(prog.TopLevelForms[0]);
    }

    [Fact]
    public void LetStarShadowing()
    {
        var prog = Build("(let* ([x 1] [x (+ x 1)]) x)");
        var outerLet = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("x", outerLet.VarName);
        var innerLet = Assert.IsType<AstNode.Let>(outerLet.Body);
        Assert.Equal("x", innerLet.VarName);
    }

    [Fact]
    public void LetBindingWithTypeAnnotation()
    {
        var prog = Build("(let [x : Int 5] (+ x 1))");
        var let = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("x", let.VarName);
        Assert.Equal(ZType.Int, let.TypeAnnotation);
        Assert.IsType<AstNode.IntLit>(let.Value);
        Assert.IsType<AstNode.Apply>(let.Body);
    }

    [Fact]
    public void LetBindingWithClrTypeAnnotation()
    {
        var prog = Build("(let [s : System.IO.Stream (new System.IO.MemoryStream)] s)");
        var let = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("s", let.VarName);
        var annotation = Assert.IsType<ZType.ZNamedType>(let.TypeAnnotation);
        Assert.Equal("System.IO.Stream", annotation.Name);
        Assert.IsType<AstNode.ClrNew>(let.Value);
    }

    [Fact]
    public void LetBindingWithoutAnnotationHasNullTypeAnnotation()
    {
        var prog = Build("(let [x 5] x)");
        var let = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Null(let.TypeAnnotation);
    }

    [Fact]
    public void LetStarWithTypeAnnotation()
    {
        var prog = Build("(let* ([x : Int 1] [y 2]) (+ x y))");
        var outerLet = Assert.IsType<AstNode.Let>(prog.TopLevelForms[0]);
        Assert.Equal("x", outerLet.VarName);
        Assert.Equal(ZType.Int, outerLet.TypeAnnotation);
        var innerLet = Assert.IsType<AstNode.Let>(outerLet.Body);
        Assert.Equal("y", innerLet.VarName);
        Assert.Null(innerLet.TypeAnnotation);
    }

    [Fact]
    public void IfExpression()
    {
        var prog = Build("(if #t 1 2)");
        var @if = Assert.IsType<AstNode.If>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.BoolLit>(@if.Condition);
        Assert.IsType<AstNode.IntLit>(@if.Then);
        Assert.IsType<AstNode.IntLit>(@if.Else);
    }

    [Fact]
    public void Lambda()
    {
        var prog = Build("(lambda (x y) (+ x y))");
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
    public void StructDecl_Basic_Parses()
    {
        var prog = Build("(struct Point [x : Int] [y : Int])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.Equal("Point", rec.RecordName);
        Assert.True(rec.IsValueType);
        Assert.Equal(2, rec.Fields.Count);
    }

    [Fact]
    public void StructDecl_Generic_Parses()
    {
        var prog = Build("(struct (Pair a b) [fst : a] [snd : b])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.Equal("Pair", rec.RecordName);
        Assert.True(rec.IsValueType);
        Assert.Equal(2, rec.TypeParams.Count);
    }

    [Fact]
    public void RecordDecl_IsValueType_DefaultsFalse()
    {
        var prog = Build("(record Point [x : Int] [y : Int])");
        var rec = Assert.IsType<AstNode.RecordDecl>(prog.TopLevelForms[0]);
        Assert.False(rec.IsValueType);
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
    public void ImportClr_InstancePropertySet()
    {
        var prog = Build("(import-clr [set-prop SomeType.Prop :instance-property-set : (SomeType Int -> Unit)])");
        var imp = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Single(imp.Imports);
        Assert.Equal("set-prop", imp.Imports[0].Alias);
        Assert.Equal(ClrImportKind.InstancePropertySet, imp.Imports[0].Kind);
    }

    [Fact]
    public void ImportClr_InstanceProperty()
    {
        var prog = Build("(import-clr [get-prop SomeType.Prop :instance-property : (SomeType -> Int)])");
        var imp = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Single(imp.Imports);
        Assert.Equal("get-prop", imp.Imports[0].Alias);
        Assert.Equal(ClrImportKind.InstanceProperty, imp.Imports[0].Kind);
    }

    [Fact]
    public void ImportClr_InstancePropertyInit()
    {
        var prog = Build("(import-clr [init-prop SomeType.Prop :instance-property-init : (SomeType Int -> Unit)])");
        var imp = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Single(imp.Imports);
        Assert.Equal("init-prop", imp.Imports[0].Alias);
        Assert.Equal(ClrImportKind.InstancePropertyInit, imp.Imports[0].Kind);
    }

    [Fact]
    public void ImportClr_InstanceIndexer()
    {
        var prog = Build("(import-clr [get-item SomeType.Item :instance-indexer : (SomeType Int -> String)])");
        var imp = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Single(imp.Imports);
        Assert.Equal("get-item", imp.Imports[0].Alias);
        Assert.Equal(ClrImportKind.InstanceIndexer, imp.Imports[0].Kind);
    }

    [Fact]
    public void ImportClr_InstanceIndexerSet()
    {
        var prog = Build(
            "(import-clr [set-item SomeType.Item :instance-indexer-set : (SomeType Int String -> Unit)])");
        var imp = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Single(imp.Imports);
        Assert.Equal("set-item", imp.Imports[0].Alias);
        Assert.Equal(ClrImportKind.InstanceIndexerSet, imp.Imports[0].Kind);
    }

    [Fact]
    public void ImportClr_SeparateColonInstanceIndexerSet()
    {
        var prog = Build(
            "(import-clr [set-item SomeType.Item : instance-indexer-set : (SomeType Int String -> Unit)])");
        var imp = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Single(imp.Imports);
        Assert.Equal("set-item", imp.Imports[0].Alias);
        Assert.Equal(ClrImportKind.InstanceIndexerSet, imp.Imports[0].Kind);
    }

    [Fact]
    public void ImportClr_WhereClauseWithNoParameters_ProducesError()
    {
        var (_, diag) = BuildWithDiagnostics("(import-clr [my-fn System.String/Concat ^a : where])");
        AssertHasError(diag, "Expected constraint list after ':where'");
    }

    [Fact]
    public void ModuleDecl()
    {
        var prog = Build("(module math/vector)");
        var mod = Assert.IsType<AstNode.ModuleDecl>(prog.TopLevelForms[0]);
        Assert.Equal("math/vector", mod.ModuleName);
        Assert.Empty(mod.Body);
    }

    [Fact]
    public void ModuleDecl_AbsorbsRemainingForms()
    {
        var prog = Build("(module foo) (define (add x y) (+ x y)) (define (sub x y) (- x y))");
        Assert.Single(prog.TopLevelForms);
        var mod = Assert.IsType<AstNode.ModuleDecl>(prog.TopLevelForms[0]);
        Assert.Equal("foo", mod.ModuleName);
        Assert.Equal(2, mod.Body.Count);
        Assert.IsType<AstNode.Define>(mod.Body[0]);
        Assert.IsType<AstNode.Define>(mod.Body[1]);
    }

    [Fact]
    public void ModuleDecl_ExplicitBody()
    {
        var prog = Build("(module foo (define (greet) \"hello\"))");
        var mod = Assert.IsType<AstNode.ModuleDecl>(prog.TopLevelForms[0]);
        Assert.Equal("foo", mod.ModuleName);
        Assert.Single(mod.Body);
        Assert.IsType<AstNode.Define>(mod.Body[0]);
    }

    [Fact]
    public void ModuleDecl_StandaloneEquivalentToExplicitBody()
    {
        var explicit_ = Build("(module a (define (get-string) \"Hello\"))");
        var standalone = Build("(module a) (define (get-string) \"Hello\")");

        Assert.Single(explicit_.TopLevelForms);
        Assert.Single(standalone.TopLevelForms);

        var explicitMod = Assert.IsType<AstNode.ModuleDecl>(explicit_.TopLevelForms[0]);
        var standaloneMod = Assert.IsType<AstNode.ModuleDecl>(standalone.TopLevelForms[0]);

        Assert.Equal("a", explicitMod.ModuleName);
        Assert.Equal("a", standaloneMod.ModuleName);
        Assert.Single(explicitMod.Body);
        Assert.Single(standaloneMod.Body);
        Assert.IsType<AstNode.Define>(explicitMod.Body[0]);
        Assert.IsType<AstNode.Define>(standaloneMod.Body[0]);
    }

    [Fact]
    public void ModuleDecl_MultipleStandalone_ProducesError()
    {
        var (_, diag) = BuildWithDiagnostics(
            "(module a) (define (get-string) \"Hello\") (module b) (define (get-string) \"Hello\")");
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Ambiguous module declaration"));
    }

    [Fact]
    public void ModuleDecl_MultipleExplicitBody_Succeeds()
    {
        var prog = Build(
            "(module a (define (get-string) \"Hello\")) (module b (define (get-string) \"Hello\"))");
        Assert.Equal(2, prog.TopLevelForms.Count);

        var modA = Assert.IsType<AstNode.ModuleDecl>(prog.TopLevelForms[0]);
        var modB = Assert.IsType<AstNode.ModuleDecl>(prog.TopLevelForms[1]);

        Assert.Equal("a", modA.ModuleName);
        Assert.Single(modA.Body);
        Assert.Equal("b", modB.ModuleName);
        Assert.Single(modB.Body);
    }

    [Fact]
    public void ImportDecl()
    {
        var prog = Build("(import geometry)");
        var imp = Assert.IsType<AstNode.Import>(prog.TopLevelForms[0]);
        Assert.Equal("geometry", imp.ModuleName);
    }

    [Fact]
    public void ImportDecl_MultipleModules()
    {
        var prog = Build("(import geometry physics)");
        Assert.Equal(2, prog.TopLevelForms.Count);
        var imp1 = Assert.IsType<AstNode.Import>(prog.TopLevelForms[0]);
        Assert.Equal("geometry", imp1.ModuleName);
        var imp2 = Assert.IsType<AstNode.Import>(prog.TopLevelForms[1]);
        Assert.Equal("physics", imp2.ModuleName);
    }

    [Fact]
    public void ImportDecl_MultipleModulesWithPaths()
    {
        var prog = Build("(import zunit stdlib/mutable-map zworld-scripts/stun-effect)");
        Assert.Equal(3, prog.TopLevelForms.Count);
        var imp1 = Assert.IsType<AstNode.Import>(prog.TopLevelForms[0]);
        Assert.Equal("zunit", imp1.ModuleName);
        var imp2 = Assert.IsType<AstNode.Import>(prog.TopLevelForms[1]);
        Assert.Equal("stdlib/mutable-map", imp2.ModuleName);
        var imp3 = Assert.IsType<AstNode.Import>(prog.TopLevelForms[2]);
        Assert.Equal("zworld-scripts/stun-effect", imp3.ModuleName);
    }

    [Fact]
    public void ListExpression_ParsesAsApply()
    {
        var prog = Build("(list 1 2 3)");
        var apply = Assert.IsType<AstNode.Apply>(prog.TopLevelForms[0]);
        Assert.Equal(3, apply.Args.Count);
        var name = Assert.IsType<AstNode.Name>(apply.Function);
        Assert.Equal("list", name.Value);
    }

    [Fact]
    public void ArrayExpression_ParsesAsApply()
    {
        var prog = Build("(array 1 2 3)");
        var apply = Assert.IsType<AstNode.Apply>(prog.TopLevelForms[0]);
        Assert.Equal(3, apply.Args.Count);
        var name = Assert.IsType<AstNode.Name>(apply.Function);
        Assert.Equal("array", name.Value);
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
    public void ClrNew_GenericType()
    {
        var prog = Build("(new (System.Collections.Generic.Dictionary String Int))");
        var clrNew = Assert.IsType<AstNode.ClrNew>(prog.TopLevelForms[0]);
        Assert.Equal("System.Collections.Generic.Dictionary", clrNew.TypeName);
        Assert.Equal(2, clrNew.TypeArgs.Count);
        Assert.Equal(ZType.String, clrNew.TypeArgs[0]);
        Assert.Equal(ZType.Int, clrNew.TypeArgs[1]);
        Assert.Empty(clrNew.Args);
    }

    [Fact]
    public void ClrNew_GenericTypeWithArgs()
    {
        var prog = Build("(new (System.Collections.Generic.List Int) 16)");
        var clrNew = Assert.IsType<AstNode.ClrNew>(prog.TopLevelForms[0]);
        Assert.Equal("System.Collections.Generic.List", clrNew.TypeName);
        Assert.Single(clrNew.TypeArgs);
        Assert.Equal(ZType.Int, clrNew.TypeArgs[0]);
        Assert.Single(clrNew.Args);
        Assert.IsType<AstNode.IntLit>(clrNew.Args[0]);
    }

    [Fact]
    public void ClrNew_NullableType()
    {
        var prog = Build("(new (Nullable System.DateTime))");
        var clrNew = Assert.IsType<AstNode.ClrNew>(prog.TopLevelForms[0]);
        Assert.Equal("System.Nullable", clrNew.TypeName);
        Assert.Single(clrNew.TypeArgs);
    }

    [Fact]
    public void ObjectExpr_SingleInterface()
    {
        var source = @"(object IComparer
  (define (Compare [x : Int] [y : Int]) : Int
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
  (define (DoFoo) : Int 42)
  (define (DoBar [x : Int]) : Int x))";
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

    [Fact]
    public void ObjectExpr_WithBaseClass()
    {
        var source = @"(object : Animal
  (define (Speak) : String ""meow""))";
        var prog = Build(source);
        var obj = Assert.IsType<AstNode.ObjectExpr>(prog.TopLevelForms[0]);
        Assert.Equal("Animal", obj.BaseClassName);
        Assert.Empty(obj.InterfaceNames);
        Assert.Single(obj.Methods);
        Assert.Equal("Speak", obj.Methods[0].Name);
    }

    [Fact]
    public void ObjectExpr_WithBaseClassAndInterfaces()
    {
        var source = @"(object : Animal ISerializable
  (define (Speak) : String ""meow"")
  (define (Serialize) : String ""...""))";
        var prog = Build(source);
        var obj = Assert.IsType<AstNode.ObjectExpr>(prog.TopLevelForms[0]);
        Assert.Equal("Animal", obj.BaseClassName);
        Assert.Single(obj.InterfaceNames);
        Assert.Equal("ISerializable", obj.InterfaceNames[0]);
        Assert.Equal(2, obj.Methods.Count);
    }

    [Fact]
    public void ObjectExpr_WithBaseClassAndGroupedInterfaces()
    {
        var source = @"(object : Animal (IFoo IBar)
  (define (Speak) : String ""meow"")
  (define (DoFoo) : Int 1)
  (define (DoBar) : Int 2))";
        var prog = Build(source);
        var obj = Assert.IsType<AstNode.ObjectExpr>(prog.TopLevelForms[0]);
        Assert.Equal("Animal", obj.BaseClassName);
        Assert.Equal(2, obj.InterfaceNames.Count);
        Assert.Equal("IFoo", obj.InterfaceNames[0]);
        Assert.Equal("IBar", obj.InterfaceNames[1]);
    }

    [Fact]
    public void ObjectExpr_WithConstructor()
    {
        var source = @"(object : Animal
  (constructor (super ""Cat"" ""meow""))
  (define (Speak) : String ""I am a cat""))";
        var prog = Build(source);
        var obj = Assert.IsType<AstNode.ObjectExpr>(prog.TopLevelForms[0]);
        Assert.Equal("Animal", obj.BaseClassName);
        Assert.NotNull(obj.Constructor);
        Assert.NotNull(obj.Constructor!.SuperArgs);
        Assert.Equal(2, obj.Constructor.SuperArgs!.Count);
        Assert.Single(obj.Methods);
    }

    [Fact]
    public void ObjectExpr_NoBaseClass_Unchanged()
    {
        var source = @"(object IFoo (define (DoFoo) : Int 42))";
        var prog = Build(source);
        var obj = Assert.IsType<AstNode.ObjectExpr>(prog.TopLevelForms[0]);
        Assert.Null(obj.BaseClassName);
        Assert.Null(obj.Constructor);
        Assert.Single(obj.InterfaceNames);
        Assert.Equal("IFoo", obj.InterfaceNames[0]);
    }

    [Fact]
    public void RaiseExpression()
    {
        var prog = Build("(raise (new System.Exception \"boom\"))");
        var raise = Assert.IsType<AstNode.Raise>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.ClrNew>(raise.Expr);
    }

    [Fact]
    public void RaiseMissingExpr_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(raise)");
        AssertHasError(diag, "'raise' requires exactly one expression");
    }

    [Fact]
    public void RaiseExtraArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(raise a b)");
        AssertHasError(diag, "'raise' requires exactly one expression");
    }

    [Fact]
    public void DefineAsync_ParsesCorrectly()
    {
        var prog = Build("(define-async (fetch [url : String]) : (Task String) url)");
        var def = Assert.IsType<AstNode.DefineAsync>(prog.TopLevelForms[0]);
        Assert.Equal("fetch", def.FnName);
        Assert.Single(def.Params);
        Assert.Equal("url", def.Params[0].Name);
        Assert.Equal(ZType.String, def.Params[0].TypeAnnotation);
        Assert.IsType<ZType.ZNamedType>(def.ReturnTypeAnnotation);
        var retType = (ZType.ZNamedType)def.ReturnTypeAnnotation!;
        Assert.Equal("Task", retType.Name);
        Assert.Single(retType.TypeArgs);
    }

    [Fact]
    public void Await_ParsesCorrectly()
    {
        var prog = Build("(await x)");
        var aw = Assert.IsType<AstNode.Await>(prog.TopLevelForms[0]);
        var name = Assert.IsType<AstNode.Name>(aw.Expr);
        Assert.Equal("x", name.Value);
    }

    [Fact]
    public void Await_RequiresOneArg_TooFew()
    {
        var (_, diag) = BuildWithDiagnostics("(await)");
        AssertHasError(diag, "'await' requires exactly one expression");
    }

    [Fact]
    public void Await_RequiresOneArg_TooMany()
    {
        var (_, diag) = BuildWithDiagnostics("(await a b)");
        AssertHasError(diag, "'await' requires exactly one expression");
    }

    // --- Attribute diagnostics ---

    [Fact]
    public void Attribute_NoTarget_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(@ Foo)");
        AssertHasError(diag, "Attribute(s) with no target declaration");
    }

    [Fact]
    public void Attribute_BadTarget_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(@ Foo) 42");
        AssertHasError(diag,
            "Attributes can only be applied to define, record, union, class, or interface declarations");
    }

    [Fact]
    public void Attribute_InvalidArg_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(@ Foo (bad))");
        AssertHasError(diag, "Invalid attribute argument");
    }

    // --- Bracket expression diagnostic ---

    [Fact]
    public void BracketExpr_InExprPosition_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("[x y]");
        AssertHasError(diag, "Unexpected bracket expression in expression position");
    }

    // --- Define diagnostics ---

    [Fact]
    public void Define_TooFewArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(define)");
        AssertHasError(diag, "'define' requires at least a name and body");
    }

    [Fact]
    public void Define_EmptySignature_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(define () x)");
        AssertHasError(diag, "Function signature must have a name");
    }

    [Fact]
    public void Define_NoBody_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(define (f [x : Int]) : Int)");
        AssertHasError(diag, "Function definition requires a body");
    }

    [Fact]
    public void Define_InvalidForm_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(define [x] 5)");
        AssertHasError(diag, "Invalid 'define' form");
    }

    // --- Let diagnostics ---

    [Fact]
    public void Let_WrongArgCount_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(let [x 5])");
        AssertHasError(diag, "'let' requires a binding and a body");
    }

    [Fact]
    public void Let_BindingNotBracket_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(let x 5)");
        AssertHasError(diag, "'let' binding must be [name expr]");
    }

    // --- Let* diagnostics ---

    [Fact]
    public void LetStar_WrongArgCount_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(let* ([x 1]))");
        AssertHasError(diag, "'let*' requires a bindings list and a body");
    }

    [Fact]
    public void LetStar_BindingsNotParens_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(let* [x 1] body)");
        AssertHasError(diag, "'let*' bindings must be a parenthesized list");
    }

    [Fact]
    public void LetStar_BindingNotBracket_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(let* ((x 1)) body)");
        AssertHasError(diag, "'let*' each binding must be [name expr]");
    }

    // --- If diagnostics ---

    [Fact]
    public void If_WrongArgCount_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(if #t 1)");
        AssertHasError(diag, "'if' requires condition, then, and else branches");
    }

    // --- Lambda diagnostics ---

    [Fact]
    public void Lambda_TooFewArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(lambda)");
        AssertHasError(diag, "'lambda' requires parameters and a body");
    }

    [Fact]
    public void Lambda_ParamsNotParens_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(lambda [x y] body)");
        AssertHasError(diag, "'lambda' parameters must be in parentheses");
    }

    // --- Match diagnostics ---

    [Fact]
    public void Match_TooFewArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(match x)");
        AssertHasError(diag, "'match' requires a scrutinee and at least one arm");
    }

    [Fact]
    public void Match_BadArm_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(match x badarm)");
        AssertHasError(diag, "Match arm must be [pattern body]");
    }

    // --- Record diagnostics ---

    [Fact]
    public void Record_NoName_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(record)");
        AssertHasError(diag, "'record' requires a name");
    }

    [Fact]
    public void Struct_NoName_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(struct)");
        AssertHasError(diag, "'struct' requires a name");
    }

    // --- Union diagnostics ---

    [Fact]
    public void Union_TooFewArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(union Shape)");
        AssertHasError(diag, "'union' requires a name and at least one case");
    }

    // --- Partial diagnostics ---

    [Fact]
    public void Partial_TooFewArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(partial f)");
        AssertHasError(diag, "'partial' requires a function and at least one argument");
    }

    // --- Namespace diagnostics ---

    [Fact]
    public void Namespace_WrongArgCount_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(namespace)");
        AssertHasError(diag, "'namespace' requires a name");
    }

    // --- Module diagnostics ---

    [Fact]
    public void Module_NoName_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(module)");
        AssertHasError(diag, "'module' requires a name");
    }

    // --- Import diagnostics ---

    [Fact]
    public void Import_WrongArgCount_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(import)");
        AssertHasError(diag, "'import' requires at least one module name");
    }

    [Fact]
    public void Import_NonAtomEntry_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(import geometry (bad))");
        AssertHasError(diag, "'import' entries must be module names");
    }

    // --- Export diagnostics ---

    [Fact]
    public void Export_NoNames_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(export)");
        AssertHasError(diag, "'export' requires at least one name");
    }

    [Fact]
    public void Export_NonNameEntry_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(export (foo))");
        AssertHasError(diag, "'export' entries must be names");
    }

    // --- Object expression diagnostics ---

    [Fact]
    public void ObjectExpr_TooFewArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(object IFoo)");
        AssertHasError(diag, "'object' requires interface name(s) and at least one method");
    }

    [Fact]
    public void ObjectExpr_BadInterfaceName_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(object (IFoo [bad]) (M : Int 1))");
        AssertHasError(diag, "Interface name must be an identifier");
    }

    [Fact]
    public void ObjectExpr_BadInterfaceSlot_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(object [x] (M : Int 1))");
        AssertHasError(diag, "'object' requires interface name(s)");
    }

    [Fact]
    public void ObjectMethod_NoBody_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(object IFoo (define (M [x : Int]) : Int))");
        AssertHasError(diag, "Method requires a body");
    }

    [Fact]
    public void ObjectMethod_BadForm_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(object IFoo badmethod)");
        AssertHasError(diag, "Method must be defined with 'define' or 'define-async'");
    }

    [Fact]
    public void ObjectMethod_BareForm_ReportsMigrationError()
    {
        var (_, diag) = BuildWithDiagnostics("(object IFoo (DoFoo : Int 42))");
        AssertHasError(diag, "Method must be defined with 'define' or 'define-async'");
    }

    [Fact]
    public void ObjectMethod_DefineForm_IsAccepted()
    {
        var prog = Build("(object IFoo (define (DoFoo) : Int 42))");
        var obj = Assert.IsType<AstNode.ObjectExpr>(prog.TopLevelForms[0]);
        Assert.Single(obj.Methods);
        Assert.Equal("DoFoo", obj.Methods[0].Name);
        Assert.False(obj.Methods[0].IsAsync);
        Assert.Empty(obj.Methods[0].Params);
        Assert.Equal(ZType.Int, obj.Methods[0].ReturnTypeAnnotation);
    }

    // --- New diagnostics ---

    [Fact]
    public void New_NoTypeName_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(new)");
        AssertHasError(diag, "'new' requires a type name");
    }

    [Fact]
    public void New_ListExpr_ParsesAsGenericType()
    {
        // (new (bad)) is now valid — parsed as a generic type with no type args
        var prog = Build("(new (bad))");
        var clrNew = Assert.IsType<AstNode.ClrNew>(prog.TopLevelForms[0]);
        Assert.Equal("bad", clrNew.TypeName);
        Assert.Empty(clrNew.TypeArgs);
        Assert.Empty(clrNew.Args);
    }

    // --- DefineAsync diagnostics ---

    [Fact]
    public void DefineAsync_TooFewArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(define-async)");
        AssertHasError(diag, "'define-async' requires a signature and body");
    }

    [Fact]
    public void DefineAsync_NoSignature_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(define-async name body)");
        AssertHasError(diag, "'define-async' requires a function signature");
    }

    [Fact]
    public void DefineAsync_NoBody_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(define-async (f [x : Int]) : Int)");
        AssertHasError(diag, "Async function definition requires a body");
    }

    // --- Class diagnostics ---

    [Fact]
    public void Class_NoName_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(class)");
        AssertHasError(diag, "'class' requires a name");
    }

    [Fact]
    public void Class_AttributeOnField_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(class Foo (@ Bar) [x : Int])");
        AssertHasError(diag, "Attributes cannot be applied to fields");
    }

    [Fact]
    public void Class_BadMember_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(class Foo badmember)");
        AssertHasError(diag, "Class member must be a field");
    }

    [Fact]
    public void Class_TrailingAttribute_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(class Foo (@ Bar))");
        AssertHasError(diag, "Attribute(s) with no target method in class body");
    }

    [Fact]
    public void Class_BareMethodForm_ReportsMigrationError()
    {
        var (_, diag) = BuildWithDiagnostics("(class Foo (Greet [] : String \"hi\"))");
        AssertHasError(diag, "Method must be defined with 'define' or 'define-async'");
    }

    [Fact]
    public void Class_DefineForm_IsAccepted()
    {
        var prog = Build("(class Foo (define (Greet) : String \"hi\"))");
        var cls = Assert.IsType<AstNode.ClassDecl>(prog.TopLevelForms[0]);
        Assert.Single(cls.Methods);
        Assert.Equal("Greet", cls.Methods[0].Name);
        Assert.False(cls.Methods[0].IsAsync);
        Assert.Equal(ZType.String, cls.Methods[0].ReturnTypeAnnotation);
    }

    [Fact]
    public void Class_AttributeAttachesToDefineMethod()
    {
        var prog = Build("(class Foo (@ Xunit.FactAttribute) (define (T) : Unit 0))");
        var cls = Assert.IsType<AstNode.ClassDecl>(prog.TopLevelForms[0]);
        Assert.Single(cls.Methods);
        Assert.Equal("T", cls.Methods[0].Name);
        Assert.NotNull(cls.Methods[0].Attributes);
        Assert.Single(cls.Methods[0].Attributes!);
        Assert.Equal("Xunit.FactAttribute", cls.Methods[0].Attributes![0].Name);
    }

    // --- Interface diagnostics ---

    [Fact]
    public void Interface_NoName_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(interface)");
        AssertHasError(diag, "'interface' requires a name");
    }

    [Fact]
    public void Interface_HasField_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(interface IFoo [x : Int])");
        AssertHasError(diag, "Interfaces cannot have fields");
    }

    [Fact]
    public void Interface_BadMember_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(interface IFoo badmember)");
        AssertHasError(diag, "Interface member must be a method signature");
    }

    [Fact]
    public void InterfaceMethod_HasBody_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(interface IFoo (M [x : Int] : Int 42))");
        AssertHasError(diag, "Interface methods cannot have a body");
    }

    [Fact]
    public void InterfaceMethod_NoReturnType_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(interface IFoo (M [x : Int]))");
        AssertHasError(diag, "Interface method requires a return type annotation");
    }

    [Fact]
    public void InterfaceMethod_BadSignature_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(interface IFoo (M))");
        AssertHasError(diag, "Method signature must be (Name [params...] : RetType)");
    }

    // --- ImportClr diagnostics ---

    [Fact]
    public void ImportClr_BadEntry_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(import-clr (bad))");
        AssertHasError(diag, "import-clr entry must be [alias qualified/Name] or a namespace");
    }

    [Fact]
    public void ImportClr_MissingTypeAfterColon_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(import-clr [foo Bar/Baz :])");
        AssertHasError(diag, "Expected type annotation after ':'");
    }

    [Fact]
    public void ImportClr_UnexpectedToken_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(import-clr [foo Bar/Baz unexpected])");
        AssertHasError(diag, "Unexpected token");
    }

    [Fact]
    public void ImportClr_BadTypeParam_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(import-clr [foo Bar/Baz (notanatom)])");
        AssertHasError(diag, "Type parameter must be an atom like ^a");
    }

    [Fact]
    public void VariadicParam_Typed()
    {
        var prog = Build("(define (fmt [s : String] [args : Object ...]) s)");
        var def = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        Assert.Equal(2, def.Params.Count);
        Assert.False(def.Params[0].IsVariadic);
        Assert.True(def.Params[1].IsVariadic);
        Assert.Equal("args", def.Params[1].Name);
    }

    [Fact]
    public void VariadicParam_Untyped()
    {
        var prog = Build("(lambda ([args ...]) 42)");
        var lam = Assert.IsType<AstNode.Lambda>(prog.TopLevelForms[0]);
        Assert.Single(lam.Params);
        Assert.True(lam.Params[0].IsVariadic);
    }

    [Fact]
    public void VariadicParam_NotLast_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(define (f [args : Int ...] [x : Int]) x)");
        AssertHasError(diag, "Variadic parameter must be the last parameter");
    }

    [Fact]
    public void VariadicParam_Multiple_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(lambda ([a ...] [b ...]) 42)");
        AssertHasError(diag, "Variadic parameter must be the last parameter");
    }

    // --- WithHandlers ---

    [Fact]
    public void WithHandlers_SingleHandler()
    {
        var prog = Build("(with-handlers ([System.Exception e] 0) 42)");
        var wh = Assert.IsType<AstNode.WithHandlers>(prog.TopLevelForms[0]);
        Assert.Single(wh.Handlers);
        Assert.Equal("System.Exception", wh.Handlers[0].ExceptionTypeName);
        Assert.Equal("e", wh.Handlers[0].BindingVarName);
        Assert.IsType<AstNode.IntLit>(wh.Handlers[0].HandlerBody);
        Assert.IsType<AstNode.IntLit>(wh.Body);
    }

    [Fact]
    public void WithHandlers_MultipleHandlers()
    {
        var source = @"(with-handlers
            ([System.DivideByZeroException _] 0)
            ([System.OverflowException _] -1)
            42)";
        var prog = Build(source);
        var wh = Assert.IsType<AstNode.WithHandlers>(prog.TopLevelForms[0]);
        Assert.Equal(2, wh.Handlers.Count);
        Assert.Equal("System.DivideByZeroException", wh.Handlers[0].ExceptionTypeName);
        Assert.Equal("System.OverflowException", wh.Handlers[1].ExceptionTypeName);
    }

    [Fact]
    public void WithHandlers_DiscardBinding()
    {
        var prog = Build("(with-handlers ([System.Exception _] 0) 42)");
        var wh = Assert.IsType<AstNode.WithHandlers>(prog.TopLevelForms[0]);
        Assert.Equal("_", wh.Handlers[0].BindingVarName);
    }

    [Fact]
    public void WithHandlers_MissingBody_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(with-handlers)");
        AssertHasError(diag, "'with-handlers' requires at least one handler and a body expression");
    }

    [Fact]
    public void WithHandlers_MissingHandler_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(with-handlers 42)");
        AssertHasError(diag, "'with-handlers' requires at least one handler and a body expression");
    }

    [Fact]
    public void WithHandlers_MalformedHandlerClause_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(with-handlers not-a-list 42)");
        AssertHasError(diag, "'with-handlers' handler must be");
    }

    [Fact]
    public void WithHandlers_MalformedBinding_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(with-handlers ([too many items] 0) 42)");
        AssertHasError(diag, "'with-handlers' handler binding must be");
    }

    [Fact]
    public void TupleNew_TwoElements()
    {
        var prog = Build("(values 1 2)");
        var tuple = Assert.IsType<AstNode.TupleNew>(prog.TopLevelForms[0]);
        Assert.Equal(2, tuple.Elements.Count);
        Assert.IsType<AstNode.IntLit>(tuple.Elements[0]);
        Assert.IsType<AstNode.IntLit>(tuple.Elements[1]);
    }

    [Fact]
    public void TupleNew_ThreeElements()
    {
        var prog = Build("(values 1 \"hello\" #t)");
        var tuple = Assert.IsType<AstNode.TupleNew>(prog.TopLevelForms[0]);
        Assert.Equal(3, tuple.Elements.Count);
    }

    [Fact]
    public void TupleNew_TooFewElements_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(values 1)");
        AssertHasError(diag, "'values' requires at least 2 elements");
    }

    [Fact]
    public void TupleType_InfixSyntax()
    {
        var prog = Build("(define (f [t : (Int * String)]) : Int 0)");
        var define = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        var paramType = define.Params[0].TypeAnnotation;
        var named = Assert.IsType<ZType.ZNamedType>(paramType);
        Assert.Equal("ValueTuple", named.Name);
        Assert.Equal(2, named.TypeArgs.Count);
    }

    [Fact]
    public void TupleType_ThreeElements()
    {
        var prog = Build("(define (f [t : (Int * String * Bool)]) : Int 0)");
        var define = Assert.IsType<AstNode.Define>(prog.TopLevelForms[0]);
        var paramType = define.Params[0].TypeAnnotation;
        var named = Assert.IsType<ZType.ZNamedType>(paramType);
        Assert.Equal("ValueTuple", named.Name);
        Assert.Equal(3, named.TypeArgs.Count);
    }

    [Fact]
    public void TuplePattern_Parsed()
    {
        var prog = Build("(match x [(values a b) 0])");
        var match = Assert.IsType<AstNode.Match>(prog.TopLevelForms[0]);
        var pattern = Assert.IsType<Pattern.Tuple>(match.Arms[0].Pattern);
        Assert.Equal(2, pattern.Elements.Count);
        Assert.IsType<Pattern.Variable>(pattern.Elements[0]);
        Assert.IsType<Pattern.Variable>(pattern.Elements[1]);
    }

    [Fact]
    public void With_Parses_SingleUpdate()
    {
        var prog = Build("(with p [x 10])");
        var with = Assert.IsType<AstNode.With>(prog.TopLevelForms[0]);
        var name = Assert.IsType<AstNode.Name>(with.Record);
        Assert.Equal("p", name.Value);
        Assert.Single(with.Updates);
        Assert.Equal("x", with.Updates[0].FieldName);
        var val = Assert.IsType<AstNode.IntLit>(with.Updates[0].Value);
        Assert.Equal(10, val.Value);
    }

    [Fact]
    public void With_Parses_MultipleUpdatesInOrder()
    {
        var prog = Build("(with p [x 1] [y 2] [z 3])");
        var with = Assert.IsType<AstNode.With>(prog.TopLevelForms[0]);
        Assert.Equal(3, with.Updates.Count);
        Assert.Equal("x", with.Updates[0].FieldName);
        Assert.Equal("y", with.Updates[1].FieldName);
        Assert.Equal("z", with.Updates[2].FieldName);
    }

    [Fact]
    public void With_Chained_ParsesAsNestedWith()
    {
        var prog = Build("(with (with p [x 1]) [y 2])");
        var outer = Assert.IsType<AstNode.With>(prog.TopLevelForms[0]);
        Assert.IsType<AstNode.With>(outer.Record);
        Assert.Single(outer.Updates);
        Assert.Equal("y", outer.Updates[0].FieldName);
    }

    [Fact]
    public void With_NotConfusedWithWithHandlers()
    {
        // Must exact-match dispatch — "with-handlers" should still work.
        var prog = Build("(with-handlers ([System.Exception e] 0) 1)");
        Assert.IsType<AstNode.WithHandlers>(prog.TopLevelForms[0]);
    }

    [Fact]
    public void With_NoRecord_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(with)");
        AssertHasError(diag, "'with' requires");
    }

    [Fact]
    public void With_NoUpdates_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(with p)");
        AssertHasError(diag, "'with' requires");
    }

    [Fact]
    public void With_MalformedBinding_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(with p (x 10))");
        AssertHasError(diag, "'with' update must be [field value]");
    }

    [Fact]
    public void With_NonAtomFieldName_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(with p [(nested-list) 10])");
        AssertHasError(diag, "'with' field name must be an identifier");
    }

    [Fact]
    public void With_DuplicateField_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(with p [x 1] [x 2])");
        AssertHasError(diag, "specifies field 'x' more than once");
    }
}
