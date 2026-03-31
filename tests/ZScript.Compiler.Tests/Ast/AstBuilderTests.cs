using Xunit;
using ZScript.Compiler.Ast;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Syntax;
using ZScript.Compiler.Types;

namespace ZScript.Compiler.Tests.Ast;

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
    public void ImportClr_InstancePropertySet()
    {
        var prog = Build("(import-clr [set-prop SomeType.Prop :instance-property-set : (Fn [SomeType Int] Unit)])");
        var imp = Assert.IsType<AstNode.ImportClr>(prog.TopLevelForms[0]);
        Assert.Single(imp.Imports);
        Assert.Equal("set-prop", imp.Imports[0].Alias);
        Assert.Equal(ClrImportKind.InstancePropertySet, imp.Imports[0].Kind);
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
    public void ListExpression()
    {
        var prog = Build("(list 1 2 3)");
        var list = Assert.IsType<AstNode.ListExpr>(prog.TopLevelForms[0]);
        Assert.Equal(3, list.Elements.Count);
    }

    [Fact]
    public void ArrayExpression()
    {
        var prog = Build("(array 1 2 3)");
        var arr = Assert.IsType<AstNode.ArrayExpr>(prog.TopLevelForms[0]);
        Assert.Equal(3, arr.Elements.Count);
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

    [Fact]
    public void ObjectExpr_WithBaseClass()
    {
        var source = @"(object : Animal
  (Speak [] : String ""meow""))";
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
  (Speak [] : String ""meow"")
  (Serialize [] : String ""...""))";
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
  (Speak [] : String ""meow"")
  (DoFoo : Int 1)
  (DoBar : Int 2))";
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
  (Speak [] : String ""I am a cat""))";
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
        var source = @"(object IFoo (DoFoo : Int 42))";
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
        AssertHasError(diag, "Attributes can only be applied to define, record, union, class, or interface declarations");
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
        var (_, diag) = BuildWithDiagnostics("(fn)");
        AssertHasError(diag, "'fn' requires parameters and a body");
    }

    [Fact]
    public void Lambda_ParamsNotBrackets_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(fn (x y) body)");
        AssertHasError(diag, "'fn' parameters must be in brackets");
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

    // --- Union diagnostics ---

    [Fact]
    public void Union_TooFewArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(union Shape)");
        AssertHasError(diag, "'union' requires a name and at least one case");
    }

    // --- Pipe diagnostics ---

    [Fact]
    public void Pipe_TooFewArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(|> x)");
        AssertHasError(diag, "'|>' requires an initial value and at least one step");
    }

    // --- Partial diagnostics ---

    [Fact]
    public void Partial_TooFewArgs_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(partial f)");
        AssertHasError(diag, "'partial' requires a function and at least one argument");
    }

    // --- Try/Catch/Propagate diagnostics ---

    [Fact]
    public void Try_WrongArgCount_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(try)");
        AssertHasError(diag, "'try' requires exactly one body expression");
    }

    [Fact]
    public void Propagate_WrongArgCount_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(?)");
        AssertHasError(diag, "'?' requires exactly one expression");
    }

    [Fact]
    public void Catch_WrongArgCount_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(catch)");
        AssertHasError(diag, "'catch' requires exactly one body expression");
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
        AssertHasError(diag, "'import' requires a module name");
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

    // --- Map diagnostics ---

    [Fact]
    public void MapExpr_BadEntry_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(map-of bad)");
        AssertHasError(diag, "map-of entry must be (key value)");
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
        var (_, diag) = BuildWithDiagnostics("(object IFoo (M [x : Int] : Int))");
        AssertHasError(diag, "Method requires a body");
    }

    [Fact]
    public void ObjectMethod_BadForm_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(object IFoo badmethod)");
        AssertHasError(diag, "Method must be (Name [params...] : RetType body)");
    }

    // --- New diagnostics ---

    [Fact]
    public void New_NoTypeName_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(new)");
        AssertHasError(diag, "'new' requires a type name");
    }

    [Fact]
    public void New_TypeNameNotIdentifier_ReportsError()
    {
        var (_, diag) = BuildWithDiagnostics("(new (bad))");
        AssertHasError(diag, "'new' type name must be an identifier");
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
}
