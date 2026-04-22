using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

public class CSharpEmitterTests
{
    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
            SuppressVersionPreamble = true,
            DisablePrelude = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath(), ["zunit"] = GetZUnitPath() },
            ModuleSearchPaths = [GetZUnitPath()],
            ModuleAliases = new Dictionary<string, string> { ["zunit"] = "zunit/zunit" }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Diagnostics));
        var csResult = (CompilationResult.CSharpOutputResult)result;
        return csResult.CsOutput;
    }

    private static CompilationResult CompileResult(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            DisablePrelude = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath(), ["zunit"] = GetZUnitPath() },
            ModuleSearchPaths = [GetZUnitPath()],
            ModuleAliases = new Dictionary<string, string> { ["zunit"] = "zunit/zunit" }
        });
        return compilation.Compile(source);
    }

    private static string NormalizeLineEndings(string s)
    {
        return s.Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static void AssertOutput(string expected, string actual)
    {
        Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(actual));
    }

    private static (string Output, DiagnosticBag Diagnostics) EmitDirect(IrNode ir)
    {
        var diag = new DiagnosticBag();
        var emitter = new CSharpEmitter(diag, "TestNameSpace", "TestClass", suppressVersionPreamble: true);
        var output = emitter.Emit(ir);
        return (output, diag);
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(CSharpEmitterTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static string GetZUnitPath()
    {
        var dir = Path.GetDirectoryName(typeof(CSharpEmitterTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "zunit", "src");
    }

    [Fact]
    public void EmitSimpleFunction()
    {
        var cs = Compile("(module test)\n(define (add [x : Int] [y : Int]) : Int (+ x y))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int Add(int x, int y)
                         {
                             return (x + y);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitIfExpression()
    {
        var cs = Compile("(module test)\n(define (abs [x : Int]) : Int (if (< x 0) (- 0 x) x))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int Abs(int x)
                         {
                             return ((x < 0) ? (0 - x) : x);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitRecursiveFunction()
    {
        var source = @"(module test)
(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int Factorial(int n, int acc)
                         {
                             while (true)
                             {
                                 if ((n == 0))
                                 {
                                     return acc;
                                 }
                                 else
                                 {
                                     var __tmp_0 = (n - 1);
                                     var __tmp_1 = (n * acc);
                                     n = __tmp_0;
                                     acc = __tmp_1;
                                     continue;
                                 }
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetBinding()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let [y (+ x 1)] (+ y 2)))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int x)
                         {
                             var y = (x + 1);
                             return (y + 2);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitBooleanExpression()
    {
        var cs = Compile("(module test)\n(define (check [a : Bool] [b : Bool]) : Bool (and a b))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static bool Check(bool a, bool b)
                         {
                             return (a && b);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitComparison()
    {
        var cs = Compile("(module test)\n(define (gt [a : Int] [b : Int]) : Bool (> a b))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static bool Gt(int a, int b)
                         {
                             return (a > b);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitNamespace()
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            Namespace = "MyGame.Logic",
            SuppressVersionPreamble = true,
            DisablePrelude = true
        });
        var result = compilation.Compile("(module test)\n(define (id [x : Int]) : Int x)");
        Assert.True(result.Success);
        var csResult = (CompilationResult.CSharpOutputResult)result;
        AssertOutput("""
                     #nullable enable

                     namespace MyGame.Logic;


                     public static class TestModule
                     {
                         public static int Id(int x)
                         {
                             return x;
                         }

                     }
                     """, csResult.CsOutput);
    }

    [Fact]
    public void EmitMultipleFunctions()
    {
        var source = @"(module test)
(define (add [x : Int] [y : Int]) : Int (+ x y))
(define (dbl [x : Int]) : Int (add x x))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int Add(int x, int y)
                         {
                             return (x + y);
                         }

                         public static int Dbl(int x)
                         {
                             return Add(x, x);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitStringReturn()
    {
        var cs = Compile("(module test)\n(define (greet [name : String]) : String name)");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static string Greet(string name)
                         {
                             return name;
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetWithClrCallBody()
    {
        var source = @"(import-clr
  [writeln System.Console/WriteLine])

(let [x ""hello""]
  (writeln x))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class UnnamedModule
                     {
                         public static string X = "hello";

                         static UnnamedModule()
                         {
                             System.Console.WriteLine(X);
                         }
                     }
                     """, cs);
    }

    [Fact]
    public void EmitNestedLetWithClrCallBody()
    {
        var source = @"(import-clr
  [writeln System.Console/WriteLine])

(let [x ""hello""]
  (let [y ""world""]
    (writeln y)))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class UnnamedModule
                     {
                         public static string X = "hello";
                         public static string Y = "world";

                         static UnnamedModule()
                         {
                             System.Console.WriteLine(y);
                         }
                     }
                     """, cs);
    }

    [Fact]
    public void NamespaceDirectiveOverridesDefault()
    {
        var cs = Compile("(module test)\n(namespace My.Game.Logic)\n(define (id [x : Int]) : Int x)");
        AssertOutput("""
                     #nullable enable

                     namespace My.Game.Logic;


                     public static class TestModule
                     {
                         public static int Id(int x)
                         {
                             return x;
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void NamespaceDirectiveOverridesCompilerOption()
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            Namespace = "From.Options",
            SuppressVersionPreamble = true,
            DisablePrelude = true
        });
        var result = compilation.Compile("(module test)\n(namespace From.Source)\n(define (id [x : Int]) : Int x)");
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Diagnostics));
        var csResult = (CompilationResult.CSharpOutputResult)result;
        AssertOutput("""
                     #nullable enable

                     namespace From.Source;


                     public static class TestModule
                     {
                         public static int Id(int x)
                         {
                             return x;
                         }

                     }
                     """, csResult.CsOutput);
    }

    [Fact]
    public void PipelineProducesValidOutput()
    {
        var source = @"(module test)
(define (square [x : Int]) : Int (* x x))";
        var compilation = new Compilation(new CompilerOptions { SuppressVersionPreamble = true, DisablePrelude = true });
        var result = compilation.Compile(source);
        Assert.True(result.Success);
        var csResult = (CompilationResult.CSharpOutputResult)result;
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int Square(int x)
                         {
                             return (x * x);
                         }

                     }
                     """, csResult.CsOutput);
    }

    [Fact]
    public void ModuleDecl_SetsClassName()
    {
        var cs = Compile("(module core)\n(define (id [x : Int]) : Int x)");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class CoreModule
                     {
                         public static int Id(int x)
                         {
                             return x;
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void ModuleDecl_HierarchicalName()
    {
        var cs = Compile("(module math/vector)\n(define (id [x : Int]) : Int x)");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class Math_VectorModule
                     {
                         public static int Id(int x)
                         {
                             return x;
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void ModuleDecl_HyphenatedName()
    {
        var cs = Compile("(module my-utils)\n(define (id [x : Int]) : Int x)");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class MyUtilsModule
                     {
                         public static int Id(int x)
                         {
                             return x;
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void NoModuleDecl_WithDefine_ReportsError()
    {
        var compilation = new Compilation(new CompilerOptions
            { OutputMode = OutputMode.CSharp, DisablePrelude = true });
        var result = compilation.Compile("(define (id [x : Int]) : Int x)");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Diagnostics, d => d.Message.Contains("require a (module ...) declaration"));
    }

    [Fact]
    public void EmitObjectExpr_SingleInterface()
    {
        var source = @"(module test)
(define (make-comparer) : IComparer
  (object IComparer
    (define (Compare [x : Int] [y : Int]) : Int
      (- x y))))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static IComparer MakeComparer()
                         {
                             return new __Object_0();
                         }


                         private sealed class __Object_0 : IComparer
                         {
                             public int Compare(int x, int y)
                             {
                                 return (x - y);
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitObjectExpr_NestedInsideMethodBody()
    {
        // An object expression nested inside another object's method body used
        // to crash the emitter with InvalidOperationException ("Collection was
        // modified") because EmitObjectClasses iterated _objectClasses with
        // foreach, and emitting a method body appended the nested object to
        // that same list. Switching to an index-based loop lets newly appended
        // classes be picked up in subsequent iterations.
        var source = @"(module test)
(interface IA (get-a : Int))
(interface IB (get-b : Int))
(define (build) : IA
  (object IA
    (define (get-a) : Int
      (let [inner : IB (object IB
                         (define (get-b) : Int 42))]
        42))))";
        var cs = Compile(source);
        Assert.Contains("__Object_0 : IA", cs);
        Assert.Contains("__Object_1 : IB", cs);
    }

    [Fact]
    public void EmitObjectExpr_MultipleInterfaces()
    {
        var source = @"(module test)
(define (make-obj) : IFoo
  (object (IFoo IBar)
    (define (DoFoo) : Int 42)
    (define (DoBar [x : Int]) : Int x)))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static IFoo MakeObj()
                         {
                             return new __Object_0();
                         }


                         private sealed class __Object_0 : IFoo, IBar
                         {
                             public int DoFoo()
                             {
                                 return 42;
                             }
                             public int DoBar(int x)
                             {
                                 return x;
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitObjectExpr_WithBaseClass()
    {
        var source = @"(module test)
(class #:open Animal
  [name : String]
  (define (Speak) : String name))

(define (make-cat) : Animal
  (object : Animal
    (define (Speak) : String ""meow"")))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public class Animal
                         {
                             public string Name { get; }

                             public Animal(string Name)
                             {
                                 this.Name = Name;
                             }

                             public virtual string Speak()
                             {
                                 return this.Name;
                             }
                         }

                         public static Animal MakeCat()
                         {
                             return new __Object_0();
                         }


                         private sealed class __Object_0 : Animal
                         {
                             public __Object_0() : base()
                             {
                             }
                             public override string Speak()
                             {
                                 return "meow";
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitObjectExpr_WithBaseClassAndInterface()
    {
        var source = @"(module test)
(interface ISerializable
  (Serialize [] : String))

(class #:open Animal
  [name : String]
  (define (Speak) : String name))

(define (make-cat) : Animal
  (object : Animal ISerializable
    (define (Speak) : String ""meow"")
    (define (Serialize) : String ""cat"")))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public interface ISerializable
                         {
                             string Serialize();
                         }

                         public class Animal
                         {
                             public string Name { get; }

                             public Animal(string Name)
                             {
                                 this.Name = Name;
                             }

                             public virtual string Speak()
                             {
                                 return this.Name;
                             }
                         }

                         public static Animal MakeCat()
                         {
                             return new __Object_0();
                         }


                         private sealed class __Object_0 : Animal, ISerializable
                         {
                             public __Object_0() : base()
                             {
                             }
                             public override string Speak()
                             {
                                 return "meow";
                             }
                             public string Serialize()
                             {
                                 return "cat";
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitObjectExpr_WithBaseClassAndConstructor()
    {
        var source = @"(module test)
(class #:open Animal
  [name : String]
  [sound : String]
  (define (Speak) : String name))

(define (make-cat) : Animal
  (object : Animal
    (constructor (super ""Cat"" ""meow""))
    (define (Speak) : String ""I am a cat"")))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public class Animal
                         {
                             public string Name { get; }
                             public string Sound { get; }

                             public Animal(string Name, string Sound)
                             {
                                 this.Name = Name;
                                 this.Sound = Sound;
                             }

                             public virtual string Speak()
                             {
                                 return this.Name;
                             }
                         }

                         public static Animal MakeCat()
                         {
                             return new __Object_0();
                         }


                         private sealed class __Object_0 : Animal
                         {
                             public __Object_0() : base("Cat", "meow")
                             {
                             }
                             public override string Speak()
                             {
                                 return "I am a cat";
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitRecord_AppearsAfterPreambleNoProgramClass()
    {
        var cs = Compile("(record Point [x : Float] [y : Float])");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public sealed record Point(float X, float Y);

                     """, cs);
    }

    [Fact]
    public void EmitStruct_EmitsRecordStruct()
    {
        var cs = Compile("(struct Point [x : Int] [y : Int])");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public readonly record struct Point(int X, int Y);

                     """, cs);
    }

    [Fact]
    public void EmitStruct_Generic_EmitsRecordStructWithTypeParams()
    {
        var cs = Compile("(struct (Box a) [value : a])");
        Assert.Contains("public readonly record struct Box<T0>(T0 Value);", cs);
    }

    [Fact]
    public void EmitStruct_New_EmitsConstructorCall()
    {
        var source = @"(module test)
(struct Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        Assert.Contains("public readonly record struct Point(int X, int Y);", cs);
        Assert.Contains("new Point(X: 0, Y: 0)", cs);
    }

    [Fact]
    public void EmitStruct_With_EmitsRecordStructWith()
    {
        var source = @"(module test)
(struct Point [x : Int] [y : Int])
(define (shift [p : Point] [nx : Int]) : Point (with p [x nx]))";
        var cs = Compile(source);
        Assert.Contains("public readonly record struct Point(int X, int Y);", cs);
        Assert.Contains("with { X = nx }", cs);
    }

    [Fact]
    public void EmitClrNew_OnUserStruct_EmitsRecordCtorCall()
    {
        // Phase-ordering fix: (new UserStruct ...) must compile to a real ctor call,
        // not a CLR reflection lookup that would fail for current-compilation types.
        var source = @"(module test)
(struct Point [x : Int] [y : Int])
(define (mk) : Point (new Point 1 2))";
        var cs = Compile(source);
        Assert.Contains("new Point(X: 1, Y: 2)", cs);
    }

    [Fact]
    public void EmitUnion_AppearsAfterPreambleNoProgramClass()
    {
        var cs = Compile("(union Shape (Circle [r : Float]) (Rect [w : Float] [h : Float]))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public abstract record Shape;
                     public sealed record Circle(float R) : Shape;
                     public sealed record Rect(float W, float H) : Shape;


                     """, cs);
    }

    [Fact]
    public void EmitRecord_PreambleComesFirst()
    {
        var cs = Compile("(record Point [x : Int])");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public sealed record Point(int X);

                     """, cs);
    }

    [Fact]
    public void EmitRecordAndFunction_CorrectOrdering()
    {
        var source = @"(module test)
(record Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public sealed record Point(int X, int Y);

                         public static Point Origin()
                         {
                             return new Point(X: 0, Y: 0);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitClassDeclOnly_NoProgramClass()
    {
        var source = @"(class Point
  [x : Int]
  [y : Int]
  (define (magnitude) : Int
    (+ (* x x) (* y y))))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public sealed class Point
                     {
                         public int X { get; }
                         public int Y { get; }

                         public Point(int X, int Y)
                         {
                             this.X = X;
                             this.Y = Y;
                         }

                         public int Magnitude()
                         {
                             return ((this.X * this.X) + (this.Y * this.Y));
                         }
                     }

                     """, cs);
    }

    [Fact]
    public void EmitClassDecl_OpenClass_NotSealed()
    {
        var source = @"(class #:open Animal
  [name : String]
  (define (Speak) : String name))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public class Animal
                     {
                         public string Name { get; }

                         public Animal(string Name)
                         {
                             this.Name = Name;
                         }

                         public virtual string Speak()
                         {
                             return this.Name;
                         }
                     }

                     """, cs);
    }

    [Fact]
    public void EmitClassDecl_Inheritance_BaseClassInList()
    {
        var source = @"(class #:open Animal
  [name : String])

(class Dog : Animal
  [breed : String])";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public class Animal
                     {
                         public string Name { get; }

                         public Animal(string Name)
                         {
                             this.Name = Name;
                         }
                     }

                     public sealed class Dog : Animal
                     {
                         public string Breed { get; }

                         public Dog(string Name, string Breed) : base(Name)
                         {
                             this.Breed = Breed;
                         }
                     }

                     """, cs);
    }

    [Fact]
    public void EmitClassDecl_Inheritance_OverrideMethod()
    {
        var source = @"(class #:open Animal
  [name : String]
  (define (Speak) : String name))

(class Dog : Animal
  [breed : String]
  (define (Speak) : String breed))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public class Animal
                     {
                         public string Name { get; }

                         public Animal(string Name)
                         {
                             this.Name = Name;
                         }

                         public virtual string Speak()
                         {
                             return this.Name;
                         }
                     }

                     public sealed class Dog : Animal
                     {
                         public string Breed { get; }

                         public Dog(string Name, string Breed) : base(Name)
                         {
                             this.Breed = Breed;
                         }

                         public override string Speak()
                         {
                             return this.Breed;
                         }
                     }

                     """, cs);
    }

    [Fact]
    public void EmitClassDecl_Inheritance_SuperMethodCall()
    {
        var source = @"(class #:open Animal
  [name : String]
  (define (Speak) : String name))

(class Dog : Animal
  (define (Speak) : String
    (string-append (super/Speak) ""!"")))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public class Animal
                     {
                         public string Name { get; }

                         public Animal(string Name)
                         {
                             this.Name = Name;
                         }

                         public virtual string Speak()
                         {
                             return this.Name;
                         }
                     }

                     public sealed class Dog : Animal
                     {

                         public Dog(string Name) : base(Name)
                         {
                         }

                         public override string Speak()
                         {
                             return (base.Speak() + "!");
                         }
                     }

                     """, cs);
    }

    [Fact]
    public void EmitClassDecl_Inheritance_BaseClassAndInterface()
    {
        var source = @"(interface IService
  (Name [] : String))

(class #:open Base
  [name : String]
  (define (Name) : String name))

(class Impl : Base IService)";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public interface IService
                     {
                         string Name();
                     }

                     public class Base
                     {
                         public string Name { get; }

                         public Base(string Name)
                         {
                             this.Name = Name;
                         }

                         public virtual string Name()
                         {
                             return this.Name;
                         }
                     }

                     public sealed class Impl : Base, IService
                     {

                         public Impl(string Name) : base(Name)
                         {
                         }
                     }

                     """, cs);
    }

    [Fact]
    public void EmitClassDecl_ExplicitConstructor_WithSuper()
    {
        var source = @"(class #:open Animal
  [name : String]
  (define (Speak) : String name))

(class Dog : Animal
  [breed : String]
  (constructor [nickname : String]
    (super nickname)
    (set! breed ""mixed"")))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public class Animal
                     {
                         public string Name { get; }

                         public Animal(string Name)
                         {
                             this.Name = Name;
                         }

                         public virtual string Speak()
                         {
                             return this.Name;
                         }
                     }

                     public sealed class Dog : Animal
                     {
                         public string Breed { get; }

                         public Dog(string nickname) : base(nickname)
                         {
                             this.Breed = "mixed";
                         }
                     }

                     """, cs);
    }

    [Fact]
    public void EmitClassDecl_ExplicitConstructor_NoBase()
    {
        var source = @"(class Widget
  [name : String]
  [size : Int]
  (constructor [n : String]
    (set! name n)
    (set! size 0)))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public sealed class Widget
                     {
                         public string Name { get; }
                         public int Size { get; }

                         public Widget(string n)
                         {
                             this.Name = n;
                             this.Size = 0;
                         }
                     }
                     """, cs);
    }

    [Fact]
    public void EmitMatch_WildcardArm_NoFallback()
    {
        var source = @"(module test)
(union Color (Red) (Green) (Blue))
(define (name [c : Color]) : Int
  (match c
    [(Red) 1]
    [(Green) 2]
    [_ 3]))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public abstract record Color;
                         public sealed record Red() : Color;
                         public sealed record Green() : Color;
                         public sealed record Blue() : Color;


                         public static int Name(Color c)
                         {
                             return c switch { Red => 1, Green => 2, _ => 3, };
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitMatch_VariableArm_NoFallback()
    {
        var source = @"(module test)
(define (describe [x : Int]) : Int
  (match x
    [0 0]
    [other other]))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int Describe(int x)
                         {
                             return x switch { 0 => 0, var other => other, };
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitMatch_NestedGenericUnionPattern_PropagatesTypeArgs()
    {
        // Inner constructor patterns (Ok, Err) nested inside Some(...) previously
        // lost their generic type arguments, producing Roslyn CS0305 ("requires
        // 2 type arguments") because `Ok<T0, T1>`/`Err<T0, T1>` can't appear as
        // bare identifiers in a pattern. The emitter now recovers each field's
        // scrutinee type from the outer union case's field template and emits
        // the fully-qualified generic case name.
        var source = @"(module test)
(import stdlib/option)
(import stdlib/result)
(define (compute) : Int
  (let [x : (Option (Result Int String)) (Some (Ok 42))]
    (match x
      [(Some (Ok v)) v]
      [(Some (Err _)) -1]
      [None -2])))";
        var cs = Compile(source);
        Assert.Contains("Stdlib_ResultModule.Ok<int, string>(var v)", cs);
        Assert.Contains("Stdlib_ResultModule.Err<int, string>(_)", cs);
    }

    [Fact]
    public void EmitClrNew_NoArgs()
    {
        var cs = Compile("(let [obj (new System.Object)] obj)");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class UnnamedModule
                     {
                         public static System.Object Obj = new System.Object();
                     }
                     """, cs);
    }

    [Fact]
    public void EmitClrNew_WithArgs()
    {
        var cs = Compile("(let [lst (new System.Collections.ArrayList 10)] lst)");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class UnnamedModule
                     {
                         public static System.Collections.ArrayList Lst = new System.Collections.ArrayList(10);
                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetInFuncBody_EmitsVarDeclaration()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let [y (+ x 1)] (+ y 2)))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int x)
                         {
                             var y = (x + 1);
                             return (y + 2);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetStarInFuncBody_EmitsVarDeclarations()
    {
        var cs = Compile(
            "(module test)\n(define (f [a : Int] [b : Int]) : Int (let* ([x (* a 2)] [y (+ x b)]) (+ x y)))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int a, int b)
                         {
                             var x = (a * 2);
                             var y = (x + b);
                             return (x + y);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetWithShadowing_StillUsesIIFE()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let* ([x (+ x 1)] [x (* x 2)]) x))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int x)
                         {
                             return ((System.Func<int, int>)((int x) => ((System.Func<int, int>)((int x) => x))((x * 2))))((x + 1));
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetWithTypeAnnotation_InFuncBody()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let [y : Int (+ x 1)] (+ y 2)))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int x)
                         {
                             int y = (x + 1);
                             return (y + 2);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetWithNullableAnnotation_InFuncBody()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let [y : Int? (+ x 1)] 42))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int x)
                         {
                             int? y = (x + 1);
                             return 42;
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetStarWithTypeAnnotations_InFuncBody()
    {
        var cs = Compile(
            "(module test)\n(define (f [x : Int]) : Int (let* ([a : Int (+ x 1)] [b : Int (* a 2)]) (+ a b)))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int x)
                         {
                             int a = (x + 1);
                             int b = (a * 2);
                             return (a + b);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetWithTypeAnnotation_TopLevel()
    {
        var source = @"(module test)
(import-clr
  [writeln System.Console/WriteLine])
(let [s : System.IO.Stream (new System.IO.MemoryStream)]
  (writeln ""created stream""))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static System.IO.Stream S = new System.IO.MemoryStream();

                         static TestModule()
                         {
                             System.Console.WriteLine("created stream");
                         }
                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetWithTypeAnnotation_ShadowingUsesIIFE()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let* ([x : Int (+ x 1)] [x : Int (* x 2)]) x))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int x)
                         {
                             return ((System.Func<int, int>)((int x) => ((System.Func<int, int>)((int x) => x))((x * 2))))((x + 1));
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitLetWithTypeAnnotation_TcoBody()
    {
        var source = @"(module test)
(define (f [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (let [m : Int (- n 1)] (f m (* n acc)))))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int n, int acc)
                         {
                             while (true)
                             {
                                 if ((n == 0))
                                 {
                                     return acc;
                                 }
                                 else
                                 {
                                     int m = (n - 1);
                                     var __tmp_0 = m;
                                     var __tmp_1 = (n * acc);
                                     n = __tmp_0;
                                     acc = __tmp_1;
                                     continue;
                                 }
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitTestCase_SingleAssertion()
    {
        var source = @"(module test)
(import zunit)
(import-clr
  [check-true Xunit.Assert/True])

(test-case booleans-work
  (check-true #t))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     using Xunit;

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         [Xunit.FactAttribute]
                         public static void BooleansWork()
                         {
                             Xunit.Assert.True(true);
                         }

                     }

                     public static class Zunit_ZunitModule
                     {
                         public static void CheckNotFalse(bool v)
                         {
                             Xunit.Assert.True(v);
                         }

                         public static void CheckPred<T0>(System.Func<T0, bool> pred, T0 v)
                         {
                             Xunit.Assert.True(pred(v));
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitTestCase_MultipleAssertions()
    {
        var source = @"(module test)
(import zunit)
(import-clr
  [check-equal Xunit.Assert/Equal ^a]
  [check-true  Xunit.Assert/True])

(test-case multiple-checks
  (check-equal 1 1)
  (check-true #t))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     using Xunit;

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         [Xunit.FactAttribute]
                         public static void MultipleChecks()
                         {
                             ((System.Func<System.ValueTuple>)(() => { Xunit.Assert.Equal(1, 1); Xunit.Assert.True(true); return default(System.ValueTuple); }))();
                         }

                     }

                     public static class Zunit_ZunitModule
                     {
                         public static void CheckNotFalse(bool v)
                         {
                             Xunit.Assert.True(v);
                         }

                         public static void CheckPred<T0>(System.Func<T0, bool> pred, T0 v)
                         {
                             Xunit.Assert.True(pred(v));
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitTestCase_WithExpression()
    {
        var source = @"(module test)
(import zunit)
(import-clr
  [check-equal Xunit.Assert/Equal ^a])

(test-case addition-works
  (check-equal (+ 1 2) 3))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     using Xunit;

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         [Xunit.FactAttribute]
                         public static void AdditionWorks()
                         {
                             Xunit.Assert.Equal(unchecked(1 + 2), 3);
                         }

                     }

                     public static class Zunit_ZunitModule
                     {
                         public static void CheckNotFalse(bool v)
                         {
                             Xunit.Assert.True(v);
                         }

                         public static void CheckPred<T0>(System.Func<T0, bool> pred, T0 v)
                         {
                             Xunit.Assert.True(pred(v));
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitTestCase_CoexistsWithDefine()
    {
        var source = @"(module test)
(import zunit)
(import-clr
  [check-equal Xunit.Assert/Equal ^a])

(define (add [x : Int] [y : Int]) : Int (+ x y))

(test-case add-works
  (check-equal (add 1 2) 3))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     using Xunit;

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int Add(int x, int y)
                         {
                             return (x + y);
                         }

                         [Xunit.FactAttribute]
                         public static void AddWorks()
                         {
                             Xunit.Assert.Equal(Add(1, 2), 3);
                         }

                     }

                     public static class Zunit_ZunitModule
                     {
                         public static void CheckNotFalse(bool v)
                         {
                             Xunit.Assert.True(v);
                         }

                         public static void CheckPred<T0>(System.Func<T0, bool> pred, T0 v)
                         {
                             Xunit.Assert.True(pred(v));
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitRaiseExpression()
    {
        var source = @"(module test)
(define (fail) : Int
  (raise (new System.Exception ""boom"")))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int Fail()
                         {
                             throw new System.Exception("boom");
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitRaiseInIfBranch()
    {
        var source = @"(module test)
(define (check [x : Int]) : Int
  (if (> x 0) x (raise (new System.ArgumentException ""negative""))))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int Check(int x)
                         {
                             return ((x > 0) ? x : throw new System.ArgumentException("negative"));
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitRaiseInFunctionBody()
    {
        var source = @"(module test)
(define (not-implemented) : Int
  (raise (new System.NotImplementedException ""todo"")))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int NotImplemented()
                         {
                             throw new System.NotImplementedException("todo");
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitAsyncFunction()
    {
        var cs = Compile("(module test)\n(define-async (compute [x : Int]) : (Task Int) (+ x 1))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static async System.Threading.Tasks.Task<int> Compute(int x)
                         {
                             return (x + 1);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitAwaitExpression()
    {
        var source = @"(module test)
(define-async (compute [x : Int]) : (Task Int) (+ x 1))
(define-async (use-it [x : Int]) : (Task Int) (await (compute x)))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static async System.Threading.Tasks.Task<int> Compute(int x)
                         {
                             return (x + 1);
                         }

                         public static async System.Threading.Tasks.Task<int> UseIt(int x)
                         {
                             return await Compute(x);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitAwaitInLet_EmitsVarStatement_NotLambda()
    {
        var source = @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int)
  (let [result (await (inner x))]
    (+ result 10)))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static async System.Threading.Tasks.Task<int> Inner(int x)
                         {
                             return (x + 1);
                         }

                         public static async System.Threading.Tasks.Task<int> Outer(int x)
                         {
                             var result = await Inner(x);
                             return (result + 10);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitAwait_NoWrappingParens()
    {
        var source = @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int) (await (inner x)))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static async System.Threading.Tasks.Task<int> Inner(int x)
                         {
                             return (x + 1);
                         }

                         public static async System.Threading.Tasks.Task<int> Outer(int x)
                         {
                             return await Inner(x);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitAsyncNonGenericTask_NoReturnStatement()
    {
        var source = @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (do-work) : Task (await (inner 42)))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static async System.Threading.Tasks.Task<int> Inner(int x)
                         {
                             return (x + 1);
                         }

                         public static async System.Threading.Tasks.Task DoWork()
                         {
                             await Inner(42);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitNestedLetWithAwait_EmitsSequentialStatements()
    {
        var source = @"(module test)
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (chain [x : Int]) : (Task Int)
  (let [a (await (step x))]
    (let [b (await (step a))]
      (+ a b))))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static async System.Threading.Tasks.Task<int> Step(int x)
                         {
                             return (x + 1);
                         }

                         public static async System.Threading.Tasks.Task<int> Chain(int x)
                         {
                             var a = await Step(x);
                             var b = await Step(a);
                             return (a + b);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitAwaitInIfBranches()
    {
        var source = @"(module test)
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (choose [flag : Bool] [x : Int]) : (Task Int)
  (if flag (await (step x)) (await (step 0))))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static async System.Threading.Tasks.Task<int> Step(int x)
                         {
                             return (x + 1);
                         }

                         public static async System.Threading.Tasks.Task<int> Choose(bool flag, int x)
                         {
                             if (flag)
                             {
                                 return await Step(x);
                             }
                             else
                             {
                                 return await Step(0);
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitAsyncWithoutAwait_EmitsReturn()
    {
        var cs = Compile("(module test)\n(define-async (simple [x : Int]) : (Task Int) (+ x 1))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static async System.Threading.Tasks.Task<int> Simple(int x)
                         {
                             return (x + 1);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitAwaitNonGenericTaskInLet()
    {
        var source = @"(module test)
(define-async (side-effect) : Task 0)
(define-async (use-it) : (Task Int)
  (let [_ (await (side-effect))]
    42))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static async System.Threading.Tasks.Task SideEffect()
                         {
                             0;
                         }

                         public static async System.Threading.Tasks.Task<int> UseIt()
                         {
                             var _ = await SideEffect();
                             return 42;
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitGenericIdentityFunction()
    {
        var cs = Compile("(module test)\n(define (id [x : ^a]) : ^a x)");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static T0 Id<T0>(T0 x)
                         {
                             return x;
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitGenericMultiTypeParams()
    {
        var cs = Compile("(module test)\n(define (const [x : ^a] [y : ^b]) : ^a x)");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static T0 Const<T0, T1>(T0 x, T1 y)
                         {
                             return x;
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitGenericHigherOrderFunction()
    {
        var cs = Compile("(module test)\n(define (apply [f : (Fn [^a] ^b)] [x : ^a]) : ^b (f x))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static T1 Apply<T0, T1>(System.Func<T0, T1> f, T0 x)
                         {
                             return f(x);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitGenericWithCollectionType()
    {
        var cs = Compile("(module test)\n(import stdlib/list)\n(define (wrap [x : ^a]) : (List ^a) (list x))");
        AssertOutput("""
                     #nullable enable

                     using System.Collections.Immutable;

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static System.Collections.Immutable.ImmutableList<T0> Wrap<T0>(T0 x)
                         {
                             return Stdlib_ListModule.List(new T0[] { x });
                         }

                     }

                     public static class Stdlib_ListModule
                     {
                         public static System.Collections.Immutable.ImmutableList<T0> List<T0>(params T0[] elements)
                         {
                             return System.Collections.Immutable.ImmutableList.Create(elements);
                         }

                         public static System.Collections.Immutable.ImmutableList<T1> List_MapLoop<T0, T1>(System.Collections.Immutable.ImmutableList<T0> xs, System.Func<T0, T1> f, int len, int i, System.Collections.Immutable.ImmutableList<T1> acc)
                         {
                             while (true)
                             {
                                 if ((i == len))
                                 {
                                     return acc;
                                 }
                                 else
                                 {
                                     var __tmp_0 = xs;
                                     var __tmp_1 = f;
                                     var __tmp_2 = len;
                                     var __tmp_3 = (i + 1);
                                     var __tmp_4 = acc.Add(f(xs[i]));
                                     xs = __tmp_0;
                                     f = __tmp_1;
                                     len = __tmp_2;
                                     i = __tmp_3;
                                     acc = __tmp_4;
                                     continue;
                                 }
                             }
                         }

                         public static System.Collections.Immutable.ImmutableList<T0> List_FilterLoop<T0>(System.Collections.Immutable.ImmutableList<T0> xs, System.Func<T0, bool> pred, int len, int i, System.Collections.Immutable.ImmutableList<T0> acc)
                         {
                             while (true)
                             {
                                 if ((i == len))
                                 {
                                     return acc;
                                 }
                                 else
                                 {
                                     var item = xs[i];
                                     if (pred(item))
                                     {
                                         var __tmp_0 = xs;
                                         var __tmp_1 = pred;
                                         var __tmp_2 = len;
                                         var __tmp_3 = (i + 1);
                                         var __tmp_4 = acc.Add(item);
                                         xs = __tmp_0;
                                         pred = __tmp_1;
                                         len = __tmp_2;
                                         i = __tmp_3;
                                         acc = __tmp_4;
                                         continue;
                                     }
                                     else
                                     {
                                         var __tmp_0 = xs;
                                         var __tmp_1 = pred;
                                         var __tmp_2 = len;
                                         var __tmp_3 = (i + 1);
                                         var __tmp_4 = acc;
                                         xs = __tmp_0;
                                         pred = __tmp_1;
                                         len = __tmp_2;
                                         i = __tmp_3;
                                         acc = __tmp_4;
                                         continue;
                                     }
                                 }
                             }
                         }

                         public static T1 List_FoldLoop<T0, T1>(System.Collections.Immutable.ImmutableList<T0> xs, System.Func<T1, T0, T1> f, int len, int i, T1 acc)
                         {
                             while (true)
                             {
                                 if ((i == len))
                                 {
                                     return acc;
                                 }
                                 else
                                 {
                                     var __tmp_0 = xs;
                                     var __tmp_1 = f;
                                     var __tmp_2 = len;
                                     var __tmp_3 = (i + 1);
                                     var __tmp_4 = f(acc, xs[i]);
                                     xs = __tmp_0;
                                     f = __tmp_1;
                                     len = __tmp_2;
                                     i = __tmp_3;
                                     acc = __tmp_4;
                                     continue;
                                 }
                             }
                         }

                         public static int List_Count<T0>(System.Collections.Immutable.ImmutableList<T0> xs)
                         {
                             return xs.Count;
                         }

                         public static T0 List_Nth<T0>(System.Collections.Immutable.ImmutableList<T0> xs, int i)
                         {
                             return xs[i];
                         }

                         public static T0 List_Head<T0>(System.Collections.Immutable.ImmutableList<T0> xs)
                         {
                             return xs[0];
                         }

                         public static System.Collections.Immutable.ImmutableList<T0> List_Tail<T0>(System.Collections.Immutable.ImmutableList<T0> xs)
                         {
                             return xs.RemoveAt(0);
                         }

                         public static System.Collections.Immutable.ImmutableList<T0> List_Cons<T0>(T0 x, System.Collections.Immutable.ImmutableList<T0> xs)
                         {
                             return xs.Insert(0, x);
                         }

                         public static System.Collections.Immutable.ImmutableList<T0> List_Append<T0>(System.Collections.Immutable.ImmutableList<T0> xs, T0 x)
                         {
                             return xs.Add(x);
                         }

                         public static System.Collections.Immutable.ImmutableList<T0> List_Concat<T0>(System.Collections.Immutable.ImmutableList<T0> xs, System.Collections.Immutable.ImmutableList<T0> ys)
                         {
                             return xs.AddRange(ys);
                         }

                         public static bool List_Empty_q<T0>(System.Collections.Immutable.ImmutableList<T0> xs)
                         {
                             return (xs.Count == 0);
                         }

                         public static System.Collections.Immutable.ImmutableList<T1> List_Map<T0, T1>(System.Collections.Immutable.ImmutableList<T0> xs, System.Func<T0, T1> f)
                         {
                             var len = xs.Count;
                             return Stdlib_ListModule.List_MapLoop(xs, f, len, 0, Stdlib_ListModule.List(System.Array.Empty<T1>()));
                         }

                         public static System.Collections.Immutable.ImmutableList<T0> List_Filter<T0>(System.Collections.Immutable.ImmutableList<T0> xs, System.Func<T0, bool> pred)
                         {
                             var len = xs.Count;
                             return Stdlib_ListModule.List_FilterLoop(xs, pred, len, 0, Stdlib_ListModule.List(System.Array.Empty<T0>()));
                         }

                         public static T1 List_Fold<T0, T1>(System.Collections.Immutable.ImmutableList<T0> xs, T1 init, System.Func<T1, T0, T1> f)
                         {
                             var len = xs.Count;
                             return Stdlib_ListModule.List_FoldLoop(xs, f, len, 0, init);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitMonomorphicFunctionHasNoTypeParams()
    {
        var cs = Compile("(module test)\n(define (add [x : Int] [y : Int]) : Int (+ x y))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int Add(int x, int y)
                         {
                             return (x + y);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitExpr_UnhandledNodeType_ReportsError()
    {
        var typeTest = new IrNode.TypeTest(
            new IrNode.Var("x") { Type = ZType.Int },
            "SomeType",
            "bound") { Type = ZType.Bool };
        var funcDef = new IrNode.FuncDef(
            "test_func",
            [new IrParam("x", ZType.Int)],
            ZType.Bool,
            typeTest,
            false);
        var seq = new IrNode.Seq([funcDef]);
        var (_, diag) = EmitDirect(seq);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics,
            d => d.IsError && d.Message.Contains("C# emission not implemented for"));
    }

    [Fact]
    public void EmitExpr_HandledNodeType_NoError()
    {
        var result = CompileResult("(module test)\n(define (add [x : Int] [y : Int]) : Int (+ x y))");
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics.Diagnostics,
            d => d.Message.Contains("C# emission not implemented"));
    }

    [Fact]
    public void TypeToCs_UnresolvedTypeVar_ReportsWarning()
    {
        var unresolvedVar = new IrNode.Var("x") { Type = new ZType.ZTypeVar(999) };
        var funcDef = new IrNode.FuncDef(
            "test_func",
            [new IrParam("x", new ZType.ZTypeVar(999))],
            new ZType.ZTypeVar(999),
            unresolvedVar,
            false);
        var seq = new IrNode.Seq([funcDef]);
        var (output, diag) = EmitDirect(seq);
        Assert.Contains(diag.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning
                 && d.Message.Contains("Unresolved type variable in C# emission"));
        Assert.Contains("object", output);
    }

    [Fact]
    public void ValidCompilation_NoSpuriousWarnings()
    {
        var source = @"(module test)
(define (add [x : Int] [y : Int]) : Int (+ x y))
(define (greet [name : String]) : String name)
(define (check [a : Bool]) : Bool (not a))";
        var result = CompileResult(source);
        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics.Diagnostics);
    }

    [Fact]
    public void EmitRecordInModule_NestedInsideModuleClass()
    {
        var source = @"(module test)
(record Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public sealed record Point(int X, int Y);

                         public static Point Origin()
                         {
                             return new Point(X: 0, Y: 0);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitUnionInModule_NestedInsideModuleClass()
    {
        var source = @"(module test)
(union Shape (Circle [r : Float]) (Rect [w : Float] [h : Float]))
(define (unit-circle) : Shape (Circle 1.0))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public abstract record Shape;
                         public sealed record Circle(float R) : Shape;
                         public sealed record Rect(float W, float H) : Shape;


                         public static Shape UnitCircle()
                         {
                             return new Circle(1f);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitClassInModule_NestedInsideModuleClass()
    {
        var source = @"(module test)
(class Point
  [x : Int]
  [y : Int]
  (define (magnitude) : Int
    (+ (* x x) (* y y))))
(define (make-point) : Point (Point 1 2))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public sealed class Point
                         {
                             public int X { get; }
                             public int Y { get; }

                             public Point(int X, int Y)
                             {
                                 this.X = X;
                                 this.Y = Y;
                             }

                             public int Magnitude()
                             {
                                 return ((this.X * this.X) + (this.Y * this.Y));
                             }
                         }

                         public static Point MakePoint()
                         {
                             return new Point(X: 1, Y: 2);
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitInterfaceInModule_NestedInsideModuleClass()
    {
        var source = @"(module test)
(interface IGreeter
  (greet [name : String] : String))
(define (make-greeter) : IGreeter
  (object (IGreeter)
    (define (greet [name : String]) : String name)))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public interface IGreeter
                         {
                             string Greet(string name);
                         }

                         public static IGreeter MakeGreeter()
                         {
                             return new __Object_0();
                         }


                         private sealed class __Object_0 : IGreeter
                         {
                             public string Greet(string name)
                             {
                                 return name;
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitRecordWithoutModule_StaysAtNamespaceLevel()
    {
        var cs = Compile("(record Point [x : Int] [y : Int])");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;

                     public sealed record Point(int X, int Y);


                     """, cs);
    }

    [Fact]
    public void EmitTypeOnlyModule_EmitsModuleClass()
    {
        var source = @"(module test)
(record Point [x : Int] [y : Int])";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public sealed record Point(int X, int Y);

                     }
                     """, cs);
    }

    [Fact]
    public void EmitVariadicFunction_EmitsParamsKeyword()
    {
        var cs = Compile(@"(define (fmt [s : String] [args : String ...]) : String s)");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class UnnamedModule
                     {
                         public static string Fmt(string s, params string[] args)
                         {
                             return s;
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitVariadicCall_EmitsArrayConstruction()
    {
        var source = @"(define (fmt [s : String] [args : String ...]) : String s)
(fmt ""hello"" ""a"" ""b"")";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class UnnamedModule
                     {
                         public static string Fmt(string s, params string[] args)
                         {
                             return s;
                         }


                         static UnnamedModule()
                         {
                             Fmt("hello", new string[] { "a", "b" });
                         }
                     }
                     """, cs);
    }

    [Fact]
    public void EmitWithHandlers_SingleHandler()
    {
        var source = @"(module test)
(define (safe-div [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.DivideByZeroException _] 0)
    (/ a b)))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int SafeDiv(int a, int b)
                         {
                             return ((System.Func<int>)(() => { try { return (a / b); } catch (System.DivideByZeroException) { return 0; } }))();
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitWithHandlers_MultipleHandlers()
    {
        var source = @"(module test)
(define (f [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.DivideByZeroException _] 0)
    ([System.OverflowException _] -1)
    (/ a b)))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int a, int b)
                         {
                             return ((System.Func<int>)(() => { try { return (a / b); } catch (System.DivideByZeroException) { return 0; } catch (System.OverflowException) { return -1; } }))();
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitWithHandlers_DiscardBinding()
    {
        var source = @"(module test)
(define (f [x : Int]) : Int
  (with-handlers
    ([System.Exception _] 0)
    x))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static int F(int x)
                         {
                             return ((System.Func<int>)(() => { try { return x; } catch (System.Exception) { return 0; } }))();
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitWithHandlers_NamedBinding()
    {
        var source = @"(module test)
(import-clr
  [ex-message System.Exception.Message :instance-property : (Fn [System.Exception] String)])

(define (f [x : Int]) : String
  (with-handlers
    ([System.Exception e] (ex-message e))
    (begin x ""ok"")))";
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static string F(int x)
                         {
                             return ((System.Func<string>)(() => { try { return ((System.Func<int, string>)((int _) => "ok"))(x); } catch (System.Exception e) { return e.Message; } }))();
                         }

                     }
                     """, cs);
    }

    // ─── Generic new ─────────────────────────────────────────────────

    [Fact]
    public void EmitClrNew_GenericType()
    {
        var cs = Compile(@"(module test)
(define (make-dict) : (Mutable-Map String Int)
  (new (System.Collections.Generic.Dictionary String Int)))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static System.Collections.Generic.Dictionary<string, int> MakeDict()
                         {
                             return new System.Collections.Generic.Dictionary<string, int>();
                         }

                     }
                     """, cs);
    }

    // ─── Out parameter support ───────────────────────────────────────

    [Fact]
    public void EmitOutParam_IntTryParse()
    {
        var cs = Compile(@"(module test)
(import-clr
  [try-parse System.Int32/TryParse])
(define (test [s : String]) : (ValueTuple Bool Int)
  (try-parse s))");
        AssertOutput("""
                     #nullable enable

                     namespace ZSchemeGenerated;


                     public static class TestModule
                     {
                         public static (bool, int) Test(string s)
                         {
                             return ((System.Func<(bool, int)>)(() => { int __out0 = default; var __ret = System.Int32.TryParse(s, out __out0); return (__ret, __out0); }))();
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void AsyncClassMethod_EmitsAsyncModifier()
    {
        var source = """
                     (module test)
                     (namespace System.Threading.Tasks)

                     (interface IWorker
                       (DoWork [x : Int] : (Task Int)))

                     (class Worker : IWorker
                       (define-async (DoWork [x : Int]) : (Task Int)
                         (+ x 1)))
                     """;
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace System.Threading.Tasks;


                     public static class TestModule
                     {
                         public interface IWorker
                         {
                             System.Threading.Tasks.Task<int> DoWork(int x);
                         }

                         public sealed class Worker : IWorker
                         {

                             public Worker()
                             {
                             }

                             public async System.Threading.Tasks.Task<int> DoWork(int x)
                             {
                                 return (x + 1);
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void AsyncClassMethod_WithAwait_EmitsAwait()
    {
        var source = """
                     (module test)
                     (namespace System.Threading.Tasks)

                     (define-async (helper [x : Int]) : (Task Int)
                       (+ x 1))

                     (class Worker
                       (define-async (DoWork [x : Int]) : (Task Int)
                         (let [result (await (helper x))]
                           (+ result 10))))
                     """;
        var cs = Compile(source);
        AssertOutput("""
                     #nullable enable

                     namespace System.Threading.Tasks;


                     public static class TestModule
                     {
                         public static async System.Threading.Tasks.Task<int> Helper(int x)
                         {
                             return (x + 1);
                         }

                         public sealed class Worker
                         {

                             public Worker()
                             {
                             }

                             public async System.Threading.Tasks.Task<int> DoWork(int x)
                             {
                                 var result = await TestModule.Helper(x);
                                 return (TestModule.Result + 10);
                             }
                         }

                     }
                     """, cs);
    }

    [Fact]
    public void EmitTupleConstruction()
    {
        var cs = Compile("(module test)\n(define pair (values 1 \"hello\"))");
        Assert.Contains("(1, \"hello\")", cs);
    }

    [Fact]
    public void EmitTupleType()
    {
        var cs = Compile(
            "(module test)\n(define (f [t : (Int * String)]) : Int (value/0 t))");
        Assert.Contains("(int, string) t", cs);
    }

    [Fact]
    public void EmitTupleAccessor()
    {
        var cs = Compile(
            "(module test)\n(define (f [t : (Int * String)]) : Int (value/0 t))");
        Assert.Contains("t.Item1", cs);
    }

    [Fact]
    public void EmitTupleAccessorSecond()
    {
        var cs = Compile(
            "(module test)\n(define (f [t : (Int * String)]) : String (value/1 t))");
        Assert.Contains("t.Item2", cs);
    }

    [Fact]
    public void EmitTuplePatternMatch()
    {
        var cs = Compile(@"(module test)
(define (swap [t : (Int * String)]) : (String * Int)
  (match t
    [(values x y) (values y x)]))");
        Assert.Contains("(var x, var y) =>", cs);
        Assert.Contains("(y, x)", cs);
    }

    [Fact]
    public void EmitTupleReturnType()
    {
        var cs = Compile(
            "(module test)\n(define (make [x : Int] [y : String]) : (Int * String) (values x y))");
        Assert.Contains("(int, string) Make", cs);
    }

    [Fact]
    public void EmitThreeElementTuple()
    {
        var cs = Compile(
            "(module test)\n(define triple (values 1 \"a\" #t))");
        Assert.Contains("(1, \"a\", true)", cs);
    }

    [Fact]
    public void EmitNestedTuple()
    {
        var cs = Compile(
            "(module test)\n(define nested (values (values 1 2) (values 3 4)))");
        Assert.Contains("((1, 2), (3, 4))", cs);
    }

    // ─── Async without await: non-generic Task and Task<T> ──────────

    [Fact]
    public void EmitAsyncWithoutAwait_NonGenericTask()
    {
        var cs = Compile("(module test)\n(define-async (do-nothing) : Task 0)");
        Assert.Contains("async System.Threading.Tasks.Task DoNothing()", cs);
    }

    [Fact]
    public void EmitAsyncWithoutAwait_TaskOfString()
    {
        var cs = Compile("(module test)\n(define-async (greet) : (Task String) \"hello\")");
        Assert.Contains("async System.Threading.Tasks.Task<string> Greet()", cs);
        Assert.Contains("return \"hello\";", cs);
    }

    // ─── Class method sibling and module-level calls ─────────────────

    [Fact]
    public void EmitClassMethod_CallsSiblingMethod()
    {
        var source = @"(module test)
(class MathHelper
  (define (Double [x : Int]) : Int (+ x x))
  (define (Quadruple [x : Int]) : Int (Double (Double x))))";
        var cs = Compile(source);
        Assert.Contains("sealed class MathHelper", cs);
        Assert.Contains("int Double(int x)", cs);
        Assert.Contains("int Quadruple(int x)", cs);
    }

    [Fact]
    public void EmitClassMethod_CallsModuleLevelDefine()
    {
        var source = @"(module test)
(define (helper [x : Int]) : Int (+ x 10))
(class Worker
  (define (Compute [x : Int]) : Int (helper x)))";
        var cs = Compile(source);
        Assert.Contains("int Helper(int x)", cs);
        Assert.Contains("sealed class Worker", cs);
        Assert.Contains("int Compute(int x)", cs);
    }

    [Fact]
    public void EmitClassMethod_RecursiveCall()
    {
        var source = @"(module test)
(class Counter
  (define (Countdown [n : Int]) : Int
    (if (= n 0) 0 (Countdown (- n 1)))))";
        var cs = Compile(source);
        Assert.Contains("sealed class Counter", cs);
        Assert.Contains("int Countdown(int n)", cs);
    }
}
