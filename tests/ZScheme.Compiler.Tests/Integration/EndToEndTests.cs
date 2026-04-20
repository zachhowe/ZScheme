using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

public class EndToEndTests
{
    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
        var csResult = (CompilationResult.CSharpOutputResult)result;
        return csResult.CsOutput;
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(EndToEndTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    [Fact]
    public void FactorialFunction()
    {
        var source = @"(module test)
(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))";
        var cs = Compile(source);
        Assert.Contains("Factorial", cs);
        Assert.Contains("while (true)", cs); // TCO
    }

    [Fact]
    public void ArithmeticExpressions()
    {
        var source = @"(module test)
(define (compute [x : Int]) : Int
  (let [a (+ x 1)]
    (let [b (* a 2)]
      (- b x))))";
        var cs = Compile(source);
        Assert.Contains("Compute", cs);
    }

    [Fact]
    public void NestedIfExpressions()
    {
        var source = @"(module test)
(define (classify [n : Int]) : Int
  (if (< n 0) -1
    (if (= n 0) 0 1)))";
        var cs = Compile(source);
        Assert.Contains("Classify", cs);
    }

    [Fact]
    public void MultipleFunctionDefinitions()
    {
        var source = @"(module test)
(define (add [x : Int] [y : Int]) : Int (+ x y))
(define (mul [x : Int] [y : Int]) : Int (* x y))
(define (combined [a : Int] [b : Int]) : Int (add (mul a b) a))";
        var cs = Compile(source);
        Assert.Contains("Add", cs);
        Assert.Contains("Mul", cs);
        Assert.Contains("Combined", cs);
    }

    [Fact]
    public void BooleanLogic()
    {
        var source = @"(module test)
(define (both [a : Bool] [b : Bool]) : Bool (and a (not b)))";
        var cs = Compile(source);
        Assert.Contains("&&", cs);
        Assert.Contains("!", cs);
    }

    [Fact]
    public void GcdFunction()
    {
        var source = @"(module test)
(define (gcd [a : Int] [b : Int]) : Int
  (if (= b 0) a (gcd b (% a b))))";
        var cs = Compile(source);
        Assert.Contains("Gcd", cs);
        Assert.Contains("while (true)", cs); // TCO
    }

    [Fact]
    public void FibonacciTailRecursive()
    {
        var source = @"(module test)
(define (fib [n : Int] [a : Int] [b : Int]) : Int
  (if (= n 0) a (fib (- n 1) b (+ a b))))";
        var cs = Compile(source);
        Assert.Contains("Fib", cs);
        Assert.Contains("while (true)", cs); // TCO
    }

    [Fact]
    public void LetStarBindings()
    {
        var source = @"(module test)
(define (compute [x : Int]) : Int
  (let* ([a (+ x 1)] [b (* a 2)] [c (- b x)])
    c))";
        var cs = Compile(source);
        Assert.Contains("Compute", cs);
    }

    [Fact]
    public void ClrInteropLetWithBody()
    {
        var source = @"
(import-clr
  [writeln System.Console/WriteLine])

(let [x ""hello""]
  (writeln x))";
        var cs = Compile(source);
        Assert.Contains("System.Console.WriteLine(X)", cs);
        Assert.Contains("static UnnamedModule()", cs);
        Assert.DoesNotContain("Main()", cs);
    }

    [Fact]
    public void LetWithTypeAnnotationUpcast()
    {
        var source = @"(module test)
(let [s : System.IO.Stream (new System.IO.MemoryStream)]
  s)";
        var cs = Compile(source);
        Assert.Contains("System.IO.Stream", cs);
    }

    [Fact]
    public void ExplicitMainFunction()
    {
        var source = @"(module test)
(import-clr
  [writeln System.Console/WriteLine])

(define (main [args : (List String)]) : Int
  (begin
    (writeln ""hello"")
    0))";
        var cs = Compile(source);
        Assert.Contains("public static int Main(string[] args)", cs);
        Assert.Contains("return Main(System.Collections.Immutable.ImmutableList.Create(args));",
            cs); // main wrapper references PascalCase inner function
    }

    [Fact]
    public void NoMainFunction_NoEntryPoint()
    {
        var source = @"(module test)
(define (add [x : Int] [y : Int]) : Int (+ x y))";
        var cs = Compile(source);
        Assert.DoesNotContain("Main(", cs);
        Assert.DoesNotContain("static TestModule()", cs);
    }

    [Fact]
    public void TopLevelLetWithBody_ProducesStaticConstructor()
    {
        var source = @"(module test)
(import-clr
  [writeln System.Console/WriteLine])

(let [x ""hello""]
  (writeln x))

(define (main [args : (List String)]) : Int 0)";
        var cs = Compile(source);
        Assert.Contains("static TestModule()", cs);
        Assert.Contains("Main(string[] args)", cs);
    }

    [Fact]
    public void NamespaceDirective()
    {
        var source = @"
(namespace My.App)

(import-clr
  [writeln System.Console/WriteLine])

(let [x ""hello""]
  (writeln x))";
        var cs = Compile(source);
        Assert.Contains("namespace My.App;", cs);
        Assert.Contains("System.Console.WriteLine(X)", cs);
    }

    [Fact]
    public void ListLiteral()
    {
        var source = @"(module test)
(import stdlib/list)
(define (make-list) : (List Int) (list 1 2 3))";
        var cs = Compile(source);
        Assert.NotNull(cs);
    }

    [Fact]
    public void OptionSomeNone()
    {
        var source = @"(module test)
(import stdlib/option)
(define (f [x : Int]) : (Option Int) (if (> x 0) (Some x) None))";
        var cs = Compile(source);
        Assert.Contains("Option", cs);
        Assert.Contains("Some", cs);
        Assert.Contains("None", cs);
    }

    [Fact]
    public void ResultOkErr()
    {
        var source = @"(module test)
(import stdlib/result)
(import stdlib/error)
(define (f [x : Int]) : (Result Int ErrorInfo) (if (> x 0) (Ok x) (Err (Error ""bad""))))";
        var cs = Compile(source);
        Assert.Contains("Result", cs);
        Assert.Contains("Ok", cs);
        Assert.Contains("Err", cs);
        Assert.Contains("ErrorInfo", cs);
    }

    [Fact]
    public void MatchOnOption()
    {
        var source = @"(module test)
(import stdlib/option)
(define (describe [opt : (Option Int)]) : String
  (match opt
    [(Some v) (string-append ""Got: "" (int->string v))]
    [None ""Nothing""]))";
        var cs = Compile(source);
        Assert.Contains("Option", cs);
        Assert.Contains("Some", cs);
        Assert.Contains("None", cs);
        Assert.Contains("switch", cs);
    }

    [Fact]
    public void MatchOnResult()
    {
        var source = @"(module test)
(import stdlib/result)
(import stdlib/error)
(define (describe [r : (Result Int ErrorInfo)]) : String
  (match r
    [(Ok v) (string-append ""Success: "" (int->string v))]
    [(Err e) ""Failed""]))";
        var cs = Compile(source);
        Assert.Contains("Result", cs);
        Assert.Contains("Ok", cs);
        Assert.Contains("Err", cs);
        Assert.Contains("switch", cs);
    }

    [Fact]
    public void IlBackendClrInteropHasCorrectAssemblyReferences()
    {
        var source = @"(module test)
(import-clr
  [writeln System.Console/WriteLine])

(define (main [args : (List String)]) : Int
  (begin
    (writeln ""hello"")
    0))";

        var compilation = new Compilation(new CompilerOptions { OutputMode = OutputMode.Il });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
        var ilResult = (CompilationResult.IlOutputResult)result;
        Assert.True(ilResult.IsExecutable);
        Assert.NotNull(ilResult.OutputBytes);

        // Verify the emitted PE references System.Runtime, not System.Private.CoreLib
        using var peReader = new PEReader(new MemoryStream(ilResult.OutputBytes));
        var metadataReader = peReader.GetMetadataReader();

        var refNames = new List<string>();
        foreach (var refHandle in metadataReader.AssemblyReferences)
        {
            var asmRef = metadataReader.GetAssemblyReference(refHandle);
            refNames.Add(metadataReader.GetString(asmRef.Name));
        }

        Assert.Contains("System.Console", refNames);
    }

    [Fact]
    public void ClrNew_InLetBinding()
    {
        var source = @"(let [obj (new System.Object)] obj)";
        var cs = Compile(source);
        Assert.Contains("new System.Object()", cs);
    }

    [Fact]
    public void ClrNew_WithImportClrMethodCall()
    {
        var source = @"
(import-clr
  [writeln System.Console/WriteLine])

(let [obj (new System.Object)]
  (writeln ""constructed""))";
        var cs = Compile(source);
        Assert.Contains("new System.Object()", cs);
        Assert.Contains("System.Console.WriteLine(\"constructed\")", cs);
    }

    [Fact]
    public void RecordConstructorInFunction()
    {
        var source = @"(module test)
(record Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        Assert.Contains("new Point(", cs);
    }

    [Fact]
    public void HigherOrderLambda()
    {
        var source = @"(module test)
(define (apply-fn [f : (Fn [Int] Int)] [x : Int]) : Int (f x))";
        var cs = Compile(source);
        Assert.Contains("System.Func<int, int>", cs);
    }

    [Fact]
    public void CatchClrException()
    {
        var source = @"(module test)
(import stdlib/option)
(import stdlib/result)
(import stdlib/error)
(import stdlib/catch)
(import-clr
  [parse-int System.Int32/Parse])

(define (safe-parse [s : String]) : (Result Int ErrorInfo)
  (catch (parse-int s)))";
        var cs = Compile(source);
        Assert.Contains("try", cs);
        Assert.Contains("catch", cs);
        Assert.Contains("Ok", cs);
        Assert.Contains("Err", cs);
    }

    [Fact]
    public void AsyncAwaitRoundTrip()
    {
        var source = @"(module test)
(define-async (compute [x : Int]) : (Task Int) (+ x 1))
(define-async (use-it [x : Int]) : (Task Int) (await (compute x)))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task<int> Compute(int x)", cs);
        Assert.Contains("async System.Threading.Tasks.Task<int> UseIt(int x)", cs);
        Assert.Contains("await", cs);
    }

    [Fact]
    public void AsyncFunctionWithoutAwait()
    {
        var source = @"(module test)
(define-async (simple [x : Int]) : (Task Int) (+ x 1))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task<int> Simple(int x)", cs);
    }

    [Fact]
    public void NestedAwait()
    {
        var source = @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int)
  (let [result (await (inner x))]
    (+ result 10)))";
        var cs = Compile(source);
        Assert.Contains("async", cs);
        Assert.Contains("await", cs);
        Assert.Contains("Inner(x)", cs);
    }

    [Fact]
    public void AwaitNonGenericTask()
    {
        var source = @"(module test)
(define-async (wait) : Task 0)
(define-async (use-wait) : (Task Int)
  (let [_ (await (wait))]
    99))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task Wait()", cs);
        Assert.Contains("await", cs);
    }

    [Fact]
    public void AwaitInLet_ProducesStatementNotLambda()
    {
        var source = @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int)
  (let [result (await (inner x))]
    (+ result 10)))";
        var cs = Compile(source);
        // Let binding with await must produce var statement, not an IIFE lambda
        Assert.Contains("var result = await Inner(x);", cs);
        // Check the outer function body has no Func<> (only check after "Outer" appears in output)
        var outerIdx = cs.IndexOf("Outer(");
        Assert.True(outerIdx >= 0);
        var outerBody = cs[outerIdx..cs.IndexOf("}", outerIdx + 1)];
        Assert.DoesNotContain("System.Func<", outerBody);
    }

    [Fact]
    public void NonGenericTask_OmitsReturn()
    {
        var source = @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (fire-and-forget) : Task
  (await (inner 1)))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task FireAndForget()", cs);
        // Non-generic Task must not return a value
        Assert.DoesNotContain("return await", cs);
    }

    [Fact]
    public void ChainedAwait_SequentialStatements()
    {
        var source = @"(module test)
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (chain [x : Int]) : (Task Int)
  (let [a (await (step x))]
    (let [b (await (step a))]
      (+ a b))))";
        var cs = Compile(source);
        Assert.Contains("var a = await Step(x);", cs);
        Assert.Contains("var b = await Step(a);", cs);
        Assert.Contains("return (a + b);", cs);
    }

    [Fact]
    public void AwaitDirectReturn_NoLambdaWrap()
    {
        var source = @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int) (await (inner x)))";
        var cs = Compile(source);
        // Direct await in body should return without lambda
        Assert.Contains("return await Inner(x);", cs);
        // Check the outer function body has no Func<>
        var outerIdx = cs.IndexOf("Outer(");
        Assert.True(outerIdx >= 0);
        var outerBody = cs[outerIdx..cs.IndexOf("}", outerIdx + 1)];
        Assert.DoesNotContain("System.Func<", outerBody);
    }

    [Fact]
    public void AwaitInIfBranches_PreservesControl()
    {
        var source = @"(module test)
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (pick [flag : Bool] [x : Int]) : (Task Int)
  (let [result (if flag (await (step x)) (await (step 0)))]
    result))";
        var cs = Compile(source);
        Assert.Contains("await Step(x)", cs);
        Assert.Contains("await Step(0)", cs);
    }

    [Fact]
    public void AwaitNonGenericInLetThenReturn()
    {
        var source = @"(module test)
(define-async (side-effect) : Task 0)
(define-async (do-then-return) : (Task Int)
  (let [_ (await (side-effect))]
    42))";
        var cs = Compile(source);
        Assert.Contains("var _ = await SideEffect();", cs);
        Assert.Contains("return 42;", cs);
    }

    [Fact]
    public void MultipleAsyncFunctions_IndependentSignatures()
    {
        var source = @"(module test)
(define-async (a [x : Int]) : (Task Int) (+ x 1))
(define-async (b [x : Int] [y : Int]) : (Task Bool) (= x y))
(define-async (c) : Task 0)";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task<int> A(int x)", cs);
        Assert.Contains("async System.Threading.Tasks.Task<bool> B(int x, int y)", cs);
        Assert.Contains("async System.Threading.Tasks.Task C()", cs);
    }

    [Fact]
    public void ClassDecl_BasicFieldsAndMethods()
    {
        var source = @"
(class Point
  [x : Int]
  [y : Int]
  (define (magnitude) : Int
    (+ (* x x) (* y y))))";
        var cs = Compile(source);
        Assert.Contains("public sealed class Point", cs);
        Assert.Contains("public int X { get; }", cs);
        Assert.Contains("public int Y { get; }", cs);
        Assert.Contains("public Point(int X, int Y)", cs);
        Assert.Contains("this.X = X;", cs);
        Assert.Contains("this.Y = Y;", cs);
        Assert.Contains("public int Magnitude()", cs);
        Assert.Contains("this.X", cs);
    }

    [Fact]
    public void ClassDecl_ConstructorAndFieldAccess()
    {
        var source = @"(module test)
(class Point
  [x : Float]
  [y : Float])
(define (get-x [p : Point]) : Float (Point/x p))";
        var cs = Compile(source);
        Assert.Contains("public sealed class Point", cs);
        Assert.Contains("p.X", cs);
    }

    [Fact]
    public void ClassDecl_MethodSlashSyntax()
    {
        var source = @"(module test)
(class Counter
  [value : Int]
  (define (next) : Int (+ value 1)))
(define (get-next [c : Counter]) : Int (Counter/next c))";
        var cs = Compile(source);
        Assert.Contains("public sealed class Counter", cs);
        Assert.Contains("c.Next()", cs);
    }

    [Fact]
    public void ClassDecl_WithTypeParameters()
    {
        var source = @"
(class (Container a)
  [value : a]
  (define (get) : a value))";
        var cs = Compile(source);
        Assert.Contains("public sealed class Container<a>", cs);
        Assert.Contains("public A Value { get; }", cs);
        Assert.Contains("public A Get()", cs);
    }

    [Fact]
    public void ClassDecl_WithInterfaces()
    {
        var source = @"
(class MyService : IDisposable
  [name : String]
  (define (GetName) : String name))";
        var cs = Compile(source);
        Assert.Contains("public sealed class MyService : IDisposable", cs);
        Assert.Contains("public string Name { get; }", cs);
        Assert.Contains("public string GetName()", cs);
    }

    [Fact]
    public void ClassDecl_ConstructorCallLowersToRecordNew()
    {
        var source = @"(module test)
(class Point
  [x : Float]
  [y : Float])
(define (make-point) : Point (Point 1.0 2.0))";
        var cs = Compile(source);
        Assert.Contains("new Point(", cs);
    }

    [Fact]
    public void ClassDecl_MethodsWithAttributes()
    {
        var source = @"
(import-clr Xunit)
(class MyTests
  (@ Xunit.FactAttribute)
  (define (RunTest) : Int 42))";
        var cs = Compile(source);
        Assert.Contains("sealed class MyTests", cs);
        Assert.Contains("[Xunit.FactAttribute]", cs);
        Assert.Contains("RunTest()", cs);
    }

    [Fact]
    public void InterfaceDecl_BasicMethods()
    {
        var source = @"
(interface IShape
  (Area [] : Float)
  (Perimeter [] : Float))";
        var cs = Compile(source);
        Assert.Contains("public interface IShape", cs);
        Assert.Contains("float Area();", cs);
        Assert.Contains("float Perimeter();", cs);
    }

    [Fact]
    public void InterfaceDecl_WithTypeParameters()
    {
        var source = @"
(interface (IContainer a)
  (Get [] : a)
  (Set [value : a] : Unit))";
        var cs = Compile(source);
        Assert.Contains("public interface IContainer<a>", cs);
        Assert.Contains("A Get();", cs);
        Assert.Contains("void Set(A value);", cs);
    }

    [Fact]
    public void InterfaceDecl_WithBaseInterfaces()
    {
        var source = @"
(interface IDrawable : IShape
  (Draw [] : Unit))";
        var cs = Compile(source);
        Assert.Contains("public interface IDrawable : IShape", cs);
        Assert.Contains("void Draw();", cs);
    }

    [Fact]
    public void InterfaceDecl_ClassImplementsInterface()
    {
        var source = @"
(interface IGreeter
  (Greet [] : String))

(class HelloGreeter : IGreeter
  [name : String]
  (define (Greet) : String name))";
        var cs = Compile(source);
        Assert.Contains("public interface IGreeter", cs);
        Assert.Contains("sealed class HelloGreeter : IGreeter", cs);
        Assert.Contains("string Greet()", cs);
    }

    [Fact]
    public void InterfaceDecl_MethodSlashSyntax()
    {
        var source = @"(module test)
(interface IShape
  (Area [] : Int))

(class Circle : IShape
  [radius : Int]
  (define (Area) : Int (* radius radius)))

(define (get-area [s : IShape]) : Int (IShape/Area s))";
        var cs = Compile(source);
        Assert.Contains("public interface IShape", cs);
        Assert.Contains("s.Area()", cs);
    }

    [Fact]
    public void InterfaceDecl_WithAttributes()
    {
        var source = @"
(@ System.ObsoleteAttribute)
(interface ILegacy
  (OldMethod [] : Int))";
        var cs = Compile(source);
        Assert.Contains("[System.ObsoleteAttribute]", cs);
        Assert.Contains("public interface ILegacy", cs);
    }

    [Fact]
    public void InterfaceDecl_MethodWithParameters()
    {
        var source = @"
(interface ICalculator
  (Add [a : Int] [b : Int] : Int)
  (Negate [x : Int] : Int))";
        var cs = Compile(source);
        Assert.Contains("public interface ICalculator", cs);
        Assert.Contains("int Add(int a, int b);", cs);
        Assert.Contains("int Negate(int x);", cs);
    }

    [Fact]
    public void ClassDecl_OpenClass()
    {
        var source = @"
(class #:open Animal
  [name : String]
  (define (Speak) : String name))";
        var cs = Compile(source);
        Assert.Contains("public class Animal", cs);
        Assert.DoesNotContain("sealed", cs);
        Assert.Contains("public virtual string Speak()", cs);
    }

    [Fact]
    public void ClassDecl_InheritanceBasicFields()
    {
        var source = @"
(class #:open Animal
  [name : String])

(class Dog : Animal
  [breed : String])";
        var cs = Compile(source);
        Assert.Contains("public class Animal", cs);
        Assert.Contains("public sealed class Dog : Animal", cs);
        Assert.Contains("public string Breed { get; }", cs);
        // Dog constructor takes base fields + own fields
        Assert.Contains("public Dog(string Name, string Breed) : base(Name)", cs);
    }

    [Fact]
    public void ClassDecl_InheritanceOverrideMethod()
    {
        var source = @"
(class #:open Animal
  [name : String]
  (define (Speak) : String name))

(class Dog : Animal
  [breed : String]
  (define (Speak) : String
    (string-append ""Woof! "" name)))";
        var cs = Compile(source);
        Assert.Contains("public virtual string Speak()", cs);
        Assert.Contains("public override string Speak()", cs);
    }

    [Fact]
    public void ClassDecl_InheritanceWithInterface()
    {
        var source = @"
(interface IService
  (Name [] : String))

(class #:open BaseService
  [name : String]
  (define (Name) : String name))

(class MyService : BaseService IService
  (define (Name) : String
    (string-append ""Service: "" name)))";
        var cs = Compile(source);
        Assert.Contains("public sealed class MyService : BaseService, IService", cs);
    }

    [Fact]
    public void ClassDecl_SuperMethodCall()
    {
        var source = @"
(class #:open Animal
  [name : String]
  (define (Speak) : String name))

(class Dog : Animal
  (define (Speak) : String
    (string-append (super/Speak) ""!"")))";
        var cs = Compile(source);
        Assert.Contains("base.Speak()", cs);
    }

    [Fact]
    public void ClassDecl_ExplicitConstructor()
    {
        var source = @"
(class #:open Animal
  [name : String]
  (constructor [raw-name : String]
    (set! name raw-name))
  (define (Speak) : String name))";
        var cs = Compile(source);
        Assert.Contains("public Animal(string rawName)", cs);
        Assert.Contains("this.Name = rawName;", cs);
    }

    [Fact]
    public void ClassDecl_ExplicitConstructorWithSuper()
    {
        var source = @"
(class #:open Animal
  [name : String]
  (define (Speak) : String name))

(class Dog : Animal
  [breed : String]
  (constructor [nickname : String]
    (super nickname)
    (set! breed ""mixed""))
  (define (Speak) : String
    (string-append ""Woof! "" name)))";
        var cs = Compile(source);
        Assert.Contains("public Dog(string nickname) : base(nickname)", cs);
        Assert.Contains("this.Breed = \"mixed\"", cs);
    }

    [Fact]
    public void ImportClr_InstanceMethod()
    {
        var source = @"(module test)
(import-clr
  [str-length System.String.Length :instance-property : (Fn [String] Int)]
  [str-substring System.String.Substring :instance : (Fn [String Int Int] String)])

(define (get-len [s : String]) : Int (str-length s))
(define (get-sub [s : String] [start : Int] [len : Int]) : String (str-substring s start len))";
        var cs = Compile(source);
        Assert.Contains("s.Length", cs);
        Assert.Contains("s.Substring(", cs);
    }

    [Fact]
    public void ImportClr_InstanceProperty()
    {
        var source = @"(module test)
(import-clr
  [list-count System.Collections.Immutable.ImmutableList.Count :instance-property : (Fn [(List ^a)] Int)])

(define (count-items [xs : (List Int)]) : Int (list-count xs))";
        var cs = Compile(source);
        Assert.Contains(".Count", cs);
    }

    [Fact]
    public void ImportClr_InstanceIndexer()
    {
        var source = @"(module test)
(import-clr
  [list-item System.Collections.Immutable.ImmutableList.Item :instance-indexer : (Fn [(List ^a) Int] ^a)])

(define (get-first [xs : (List Int)]) : Int (list-item xs 0))";
        var cs = Compile(source);
        Assert.Contains("[0]", cs);
    }

    [Fact]
    public void ImportClr_SubtypePassedAsSupertype()
    {
        var source = @"(module test)
(import-clr
  [stream-length System.IO.Stream.Length
    :instance-property : (Fn [System.IO.Stream] Int)])

(define (get-length [s : System.IO.Stream]) : Int
  (stream-length s))

(define (test) : Int
  (get-length (new System.IO.MemoryStream)))";
        var cs = Compile(source);
        Assert.Contains(".Length", cs);
    }

    [Fact]
    public void ImportClr_InstancePropertySet()
    {
        var source = @"(module test)
(import-clr
  [set-base-addr System.Net.Http.HttpRequestMessage.Content
    :instance-property-set : (Fn [System.Net.Http.HttpRequestMessage System.Net.Http.HttpContent] Unit)])

(define (set-content [msg : System.Net.Http.HttpRequestMessage] [c : System.Net.Http.HttpContent]) : Unit
  (set-base-addr msg c))";
        var cs = Compile(source);
        Assert.Contains(".Content = ", cs);
    }

    [Fact]
    public void ImportClr_InstancePropertyInit()
    {
        var source = @"(module test)
(import-clr
  [set-base-addr System.Net.Http.HttpRequestMessage.Content
    :instance-property-init : (Fn [System.Net.Http.HttpRequestMessage System.Net.Http.HttpContent] Unit)])

(define (set-content [msg : System.Net.Http.HttpRequestMessage] [c : System.Net.Http.HttpContent]) : Unit
  (set-base-addr msg c))";
        var cs = Compile(source);
        Assert.Contains(".Content = ", cs);
    }

    [Fact]
    public void ClassDecl_InitFields_HaveInitAccessors()
    {
        var source = @"(module test)
(class Config
  [host : String #:init]
  [port : Int #:init])";
        var cs = Compile(source);
        Assert.Contains("public string Host { get; init; }", cs);
        Assert.Contains("public int Port { get; init; }", cs);
    }

    [Fact]
    public void ClassDecl_MutableFields_HaveSetAccessors()
    {
        var source = @"(module test)
(class Counter
  [count : Int #:mutable]
  (define (Increment) : Unit
    (set! count (+ count 1))))";
        var cs = Compile(source);
        Assert.Contains("public int Count { get; set; }", cs);
    }

    [Fact]
    public void MutableArrayToArray_Conversion()
    {
        var source = @"(module test)
(define (test [arr : (Mutable-Array Int)]) : (Array Int)
  (mutable-array->array arr))";
        var cs = Compile(source);
        Assert.Contains("ImmutableArray.Create(", cs);
    }

    [Fact]
    public void ArrayToMutableArray_Conversion()
    {
        var source = @"(module test)
(define (test [a : (Array Int)]) : (Mutable-Array Int)
  (array->mutable-array a))";
        var cs = Compile(source);
        Assert.Contains("System.Linq.Enumerable.ToArray(", cs);
    }

    [Fact]
    public void MutableListToList_Conversion()
    {
        var source = @"(module test)
(define (test [ml : (Mutable-List Int)]) : (List Int)
  (mutable-list->list ml))";
        var cs = Compile(source);
        Assert.Contains("ImmutableList.CreateRange(", cs);
    }

    [Fact]
    public void ListToMutableList_Conversion()
    {
        var source = @"(module test)
(define (test [l : (List Int)]) : (Mutable-List Int)
  (list->mutable-list l))";
        var cs = Compile(source);
        Assert.Contains("System.Linq.Enumerable.ToList(", cs);
    }

    [Fact]
    public void MutableMapToMap_Conversion()
    {
        var source = @"(module test)
(define (test [mm : (Mutable-Map String Int)]) : (Map String Int)
  (mutable-map->map mm))";
        var cs = Compile(source);
        Assert.Contains("ImmutableDictionary.CreateRange(", cs);
    }

    [Fact]
    public void MapToMutableMap_Conversion()
    {
        var source = @"(module test)
(define (test [m : (Map String Int)]) : (Mutable-Map String Int)
  (map->mutable-map m))";
        var cs = Compile(source);
        Assert.Contains("new System.Collections.Generic.Dictionary(", cs);
    }

    // ─── Generic new ─────────────────────────────────────────────────

    [Fact]
    public void GenericNew_Dictionary()
    {
        var source = @"(module test)
(define (make-dict) : (Mutable-Map String Int)
  (new (System.Collections.Generic.Dictionary String Int)))";
        var cs = Compile(source);
        Assert.Contains("new System.Collections.Generic.Dictionary<string, int>()", cs);
    }

    [Fact]
    public void GenericNew_List()
    {
        var source = @"(module test)
(define (make-list) : (Mutable-List Int)
  (new (System.Collections.Generic.List Int)))";
        var cs = Compile(source);
        Assert.Contains("new System.Collections.Generic.List<int>()", cs);
    }

    // ─── Out parameter support ───────────────────────────────────────

    [Fact]
    public void OutParam_IntTryParse()
    {
        var source = @"(module test)
(import-clr
  [try-parse System.Int32/TryParse])
(define (test [s : String]) : (ValueTuple Bool Int)
  (try-parse s))";
        var cs = Compile(source);
        Assert.Contains("out", cs);
        Assert.Contains("TryParse", cs);
    }

    // ─── set! in method bodies ──────────────────────────────────────

    [Fact]
    public void SetField_MutableFieldInMethodBody()
    {
        var source = @"
(class Counter
  [count : Int #:mutable]
  (define (Increment) : Unit
    (set! count (+ count 1))))";
        var cs = Compile(source);
        Assert.Contains("this.Count = (this.Count + 1)", cs);
    }

    [Fact]
    public void SetField_MutableFieldInBeginBlock()
    {
        var source = @"
(class Counter
  [count : Int #:mutable]
  (define (Reset) : Unit
    (begin
      (set! count 0))))";
        var cs = Compile(source);
        Assert.Contains("this.Count = 0", cs);
    }

    [Fact]
    public void SetField_ImmutableFieldErrors()
    {
        var source = @"
(class Foo
  [name : String]
  (define (SetName [n : String]) : Unit
    (set! name n)))";
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Diagnostics, d => d.Message.Contains("Cannot set! immutable field"));
    }

    [Fact]
    public void SetField_UnknownFieldErrors()
    {
        var source = @"
(class Foo
  [name : String]
  (define (SetName [n : String]) : Unit
    (set! unknown n)))";
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Diagnostics, d => d.Message.Contains("Unknown field"));
    }

    [Fact]
    public void PolymorphicEquality_NullCheck_Il()
    {
        var source = @"(module test)
(define (is-null? [x : String]) : Bool
  (= x null))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes().SelectMany(t => t.GetMethods())
            .First(m => m.Name.Contains("null", StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == 1);
        Assert.Equal(true, method.Invoke(null, [null]));
        Assert.Equal(false, method.Invoke(null, ["hello"]));
    }

    [Fact]
    public void PolymorphicEquality_StringComparison_Il()
    {
        var source = @"(module test)
(define (same? [a : String] [b : String]) : Bool
  (= a b))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
    }

    [Fact]
    public void BoxingToSystemObject_CSharp()
    {
        var source = @"
(import stdlib/mutable/map)

(define (put-float [m : (Mutable-Map String System.Object)] [v : Float]) : Unit
  (mutable-map/put! m ""key"" v))";
        var cs = Compile(source);
        Assert.Contains("PutFloat", cs);
    }

    [Fact]
    public void NullableWidening_FloatToNullableFloat_CSharp()
    {
        var source = @"
(class Timer
  [duration : Float? #:mutable]
  (constructor
    (set! duration 3.0)))";
        var cs = Compile(source);
        Assert.Contains("Duration", cs);
    }

    [Fact]
    public void NullableWidening_FloatToNullableFloat_Il()
    {
        var source = @"(module test)
(class Timer
  [duration : Float? #:mutable]
  (constructor
    (set! duration 3.0))
  (define (GetDuration) : Float? duration))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        // Load and verify the type can be instantiated
        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var timerType = asm.GetExportedTypes().First(t => t.Name == "Timer");
        var instance = Activator.CreateInstance(timerType)!;
        var getDuration = timerType.GetMethod("GetDuration")!;
        var value = getDuration.Invoke(instance, []);
        Assert.Equal(3.0f, value);
    }

    [Fact]
    public void NullableWidening_NullToNullableFloat_Il()
    {
        var source = @"(module test)
(class Timer
  [duration : Float? #:mutable]
  (constructor
    (set! duration null))
  (define (GetDuration) : Float? duration))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var timerType = asm.GetExportedTypes().First(t => t.Name == "Timer");
        var instance = Activator.CreateInstance(timerType)!;
        var getDuration = timerType.GetMethod("GetDuration")!;
        var value = getDuration.Invoke(instance, []);
        Assert.Null(value);
    }

    [Fact]
    public void NullableWidening_SetFieldAfterConstruction_Il()
    {
        var source = @"(module test)
(class Counter
  [value : Int? #:mutable]
  (constructor
    (set! value null))
  (define (SetValue [v : Int]) : Unit
    (set! value v))
  (define (GetValue) : Int? value))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var counterType = asm.GetExportedTypes().First(t => t.Name == "Counter");
        var instance = Activator.CreateInstance(counterType)!;

        // Initially null
        var getValue = counterType.GetMethod("GetValue")!;
        Assert.Null(getValue.Invoke(instance, []));

        // After setting to 42, should be 42
        var setValue = counterType.GetMethod("SetValue")!;
        setValue.Invoke(instance, [42]);
        Assert.Equal(42, getValue.Invoke(instance, []));
    }

    // ===== Static field / enum fallback end-to-end tests =====

    [Fact]
    public void EnumAccess_DayOfWeek_Il()
    {
        var source = @"(module test)
(import-clr
  [friday System.DayOfWeek/Friday
    : (Fn [] System.DayOfWeek)])

(define (get-friday) : System.DayOfWeek
  (friday))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes().SelectMany(t => t.GetMethods())
            .First(m => m.Name.Contains("Friday", StringComparison.OrdinalIgnoreCase));
        var value = method.Invoke(null, []);
        Assert.Equal(DayOfWeek.Friday, value);
    }

    [Fact]
    public void StaticField_StringEmpty_Il()
    {
        var source = @"(module test)
(import-clr
  [empty-string System.String/Empty
    : (Fn [] String)])

(define (get-empty) : String
  (empty-string))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes().SelectMany(t => t.GetMethods())
            .First(m => m.Name.Contains("Empty", StringComparison.OrdinalIgnoreCase));
        var value = method.Invoke(null, []);
        Assert.Equal("", value);
    }

    // ===== Boxing end-to-end tests =====

    [Fact]
    public void Boxing_FloatToObject_InDictionary_Il()
    {
        // Test that Float can be stored in a Dictionary<string, object> via mutable-map/put!
        var source = @"(module test)
(import stdlib/mutable/map)

(define (store-float) : (Mutable-Map String System.Object)
  (let [m (mutable-map/new)]
    (begin
      (mutable-map/put! m ""key"" 3.14)
      m)))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
    }

    [Fact]
    public void Boxing_IntToObject_ViaClrCall_Il()
    {
        // Test that Int can be passed to a CLR method expecting System.Object
        var source = @"(module test)
(import-clr
  [writeln System.Console/WriteLine : (Fn [System.Object] Unit)])

(define (log-int [v : Int]) : Unit
  (writeln v))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
    }

    // ===== Nullable wrapping end-to-end tests with runtime verification =====

    [Fact]
    public void NullableWidening_MultipleFields_Il()
    {
        var source = @"(module test)
(class Effect
  [name : String #:mutable]
  [duration : Float? #:mutable]
  [delay : Float? #:mutable]

  (constructor
    (set! name ""Test"")
    (set! duration 5.0)
    (set! delay null))

  (define (GetName) : String name)
  (define (GetDuration) : Float? duration)
  (define (GetDelay) : Float? delay))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var effectType = asm.GetExportedTypes().First(t => t.Name == "Effect");
        var instance = Activator.CreateInstance(effectType)!;

        Assert.Equal("Test", effectType.GetMethod("GetName")!.Invoke(instance, []));
        Assert.Equal(5.0f, effectType.GetMethod("GetDuration")!.Invoke(instance, []));
        Assert.Null(effectType.GetMethod("GetDelay")!.Invoke(instance, []));
    }

    [Fact]
    public void PolymorphicEquality_IntComparison_Il()
    {
        var source = @"(module test)
(define (eq? [a : Int] [b : Int]) : Bool
  (= a b))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes().SelectMany(t => t.GetMethods())
            .First(m => m.GetParameters().Length == 2 && m.ReturnType == typeof(bool));
        Assert.Equal(true, method.Invoke(null, [5, 5]));
        Assert.Equal(false, method.Invoke(null, [5, 7]));
    }

    [Fact]
    public void NullableReceiver_PropertyAccess_Il()
    {
        // Regression test: property access on a nullable receiver type should resolve
        // the property on the unwrapped type, not emit ldc.i4.0 fallback
        var source = @"(module test)
(import-clr
  [uri-host System.Uri.Host
    :instance-property : (Fn [System.Uri] String)]
  System)

(define (get-host [u : System.Uri?]) : String
  (if (= u null) ""none"" (uri-host u)))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes().SelectMany(t => t.GetMethods())
            .First(m => m.Name.Contains("GetHost") || m.Name.Contains("Get_host"));

        // Null input → "none"
        Assert.Equal("none", method.Invoke(null, [null]));

        // Non-null input → host string
        var uri = new Uri("https://example.com/path");
        Assert.Equal("example.com", method.Invoke(null, [uri]));
    }


   [Fact]
    public void ClassDecl_SingleClrInterface_ImplementsInterface_Il()
    {
        var source = @"
(class MyDisposable : System.IDisposable
  [disposed : Bool #:mutable]
  (constructor (set! disposed #f))
  (define (Dispose) : Unit
    (set! disposed #t)))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = System.Reflection.Assembly.Load(ilResult.OutputBytes);
        var type = asm.GetExportedTypes().First(t => t.Name == "MyDisposable");

        Assert.True(typeof(IDisposable).IsAssignableFrom(type),
            $"Expected MyDisposable to implement IDisposable. Interfaces: [{string.Join(", ", type.GetInterfaces().Select(i => i.Name))}]");
        Assert.Contains(typeof(IDisposable), type.GetInterfaces());
    }

    [Fact]
    public void ClassDecl_ZSchemeInterface_ImplementsInterface_Il()
    {
        var source = @"
(interface IGreeter
  (Greet [] : String))

(class HelloGreeter : IGreeter
  [name : String]
  (define (Greet) : String name))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = System.Reflection.Assembly.Load(ilResult.OutputBytes);
        var greeterInterface = asm.GetExportedTypes().First(t => t.Name == "IGreeter");
        var helloType = asm.GetExportedTypes().First(t => t.Name == "HelloGreeter");

        Assert.True(greeterInterface.IsAssignableFrom(helloType),
            "Expected HelloGreeter to implement IGreeter");
    }

    [Fact]
    public void With_Expression_EmitsCSharpWith()
    {
        var source = @"(module test)
(record Point [x : Int] [y : Int])
(define (shift [p : Point] [nx : Int]) : Point
  (with p [x nx]))";
        var cs = Compile(source);
        Assert.Contains(" with { X = nx }", cs);
    }

    [Fact]
    public void With_MultipleFields_EmitsCSharpWith()
    {
        var source = @"(module test)
(record Point [x : Int] [y : Int])
(define (move [p : Point] [nx : Int] [ny : Int]) : Point
  (with p [x nx] [y ny]))";
        var cs = Compile(source);
        Assert.Contains(" with { X = nx, Y = ny }", cs);
    }

    [Fact]
    public void With_Expression_Il_RoundtripExecutes()
    {
        var source = @"
(record Point [x : Int] [y : Int])
(define (shift-x [p : Point] [nx : Int]) : Point
  (with p [x nx]))
(define (move-to [p : Point] [nx : Int] [ny : Int]) : Point
  (with p [x nx] [y ny]))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = System.Reflection.Assembly.Load(ilResult.OutputBytes);
        var pointType = asm.GetExportedTypes().First(t => t.Name == "Point");
        var moduleType = asm.GetExportedTypes().First(t => t.Name.EndsWith("Module"));

        // Has <Clone>$ method (required for decompilers to render `with`).
        Assert.NotNull(pointType.GetMethod("<Clone>$",
            BindingFlags.Public | BindingFlags.Instance));
        // Has copy constructor.
        Assert.NotNull(pointType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, [pointType]));
        // Has PrintMembers method.
        Assert.NotNull(pointType.GetMethod("PrintMembers",
            BindingFlags.Instance | BindingFlags.NonPublic));
        // Has EqualityContract.
        Assert.NotNull(pointType.GetProperty("EqualityContract",
            BindingFlags.Instance | BindingFlags.NonPublic));

        // Runtime check: with actually clones and updates.
        var ctor = pointType.GetConstructor([typeof(int), typeof(int)])!;
        var original = ctor.Invoke([1, 2]);
        var shift = moduleType.GetMethod("ShiftX")!;
        var shifted = shift.Invoke(null, [original, 99]);
        Assert.NotSame(original, shifted);
        Assert.Equal(99, pointType.GetProperty("X")!.GetValue(shifted));
        Assert.Equal(2, pointType.GetProperty("Y")!.GetValue(shifted));
        // Original untouched.
        Assert.Equal(1, pointType.GetProperty("X")!.GetValue(original));

        var moveTo = moduleType.GetMethod("MoveTo")!;
        var moved = moveTo.Invoke(null, [original, 10, 20]);
        Assert.Equal(10, pointType.GetProperty("X")!.GetValue(moved));
        Assert.Equal(20, pointType.GetProperty("Y")!.GetValue(moved));
    }

    // ─── struct ──────────────────────────────────────────────────────

    [Fact]
    public void Struct_EmitsCSharpRecordStruct()
    {
        var source = @"(module test)
(struct Point [x : Int] [y : Int])";
        var cs = Compile(source);
        Assert.Contains("public readonly record struct Point(int X, int Y);", cs);
    }

    [Fact]
    public void Struct_NewForm_EmitsCtorCall()
    {
        // Verifies the (new ...) phase-ordering fix: user-defined struct names resolve
        // through the record-ctor path rather than CLR reflection.
        var source = @"(module test)
(struct Point [x : Int] [y : Int])
(define (mk) : Point (new Point 3 4))";
        var cs = Compile(source);
        Assert.Contains("new Point(X: 3, Y: 4)", cs);
    }

    [Fact]
    public void Struct_With_EmitsCSharpWithExpression()
    {
        var source = @"(module test)
(struct Point [x : Int] [y : Int])
(define (shift [p : Point] [nx : Int]) : Point (with p [x nx]))";
        var cs = Compile(source);
        Assert.Contains("with { X = nx }", cs);
    }

    [Fact]
    public void Struct_Il_RoundtripExecutes_ValueSemantics()
    {
        // The defining test for value semantics: shifting a Point produces a fresh value;
        // the source must remain unchanged because structs are stack-copied.
        var source = @"
(struct Point [x : Int] [y : Int])
(define (shift-x [p : Point] [nx : Int]) : Point (with p [x nx]))
(define (move-to [p : Point] [nx : Int] [ny : Int]) : Point
  (with p [x nx] [y ny]))";

        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = System.Reflection.Assembly.Load(ilResult.OutputBytes);
        var pointType = asm.GetExportedTypes().First(t => t.Name == "Point");
        var moduleType = asm.GetExportedTypes().First(t => t.Name.EndsWith("Module"));

        // Real CLR struct.
        Assert.True(pointType.IsValueType);
        Assert.Equal(typeof(ValueType), pointType.BaseType);
        // No <Clone>$ on structs.
        Assert.Null(pointType.GetMethod("<Clone>$",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        var ctor = pointType.GetConstructor([typeof(int), typeof(int)])!;
        var original = ctor.Invoke([1, 2]);
        var shift = moduleType.GetMethod("ShiftX")!;
        var shifted = shift.Invoke(null, [original, 99]);
        Assert.Equal(99, pointType.GetProperty("X")!.GetValue(shifted));
        Assert.Equal(2, pointType.GetProperty("Y")!.GetValue(shifted));
        // Value semantics: passing the struct to ShiftX did not mutate the original.
        Assert.Equal(1, pointType.GetProperty("X")!.GetValue(original));
        Assert.Equal(2, pointType.GetProperty("Y")!.GetValue(original));

        var moveTo = moduleType.GetMethod("MoveTo")!;
        var moved = moveTo.Invoke(null, [original, 10, 20]);
        Assert.Equal(10, pointType.GetProperty("X")!.GetValue(moved));
        Assert.Equal(20, pointType.GetProperty("Y")!.GetValue(moved));
    }

    [Fact]
    public void NewForm_OnUserRecord_Il_RoundtripExecutes()
    {
        // Regression guard for the (new ...) phase-ordering fix: previously this would
        // fail because ClrInterop.FindType cannot see types from the current compilation.
        var source = @"
(record Point [x : Int] [y : Int])
(define (mk) : Point (new Point 3 4))";
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = System.Reflection.Assembly.Load(ilResult.OutputBytes);
        var pointType = asm.GetExportedTypes().First(t => t.Name == "Point");
        var moduleType = asm.GetExportedTypes().First(t => t.Name.EndsWith("Module"));

        var made = moduleType.GetMethod("Mk")!.Invoke(null, []);
        Assert.NotNull(made);
        Assert.Equal(3, pointType.GetProperty("X")!.GetValue(made));
        Assert.Equal(4, pointType.GetProperty("Y")!.GetValue(made));
    }

    // ─── Async without await: non-generic Task and Task<T> ──────────

    [Fact]
    public void AsyncFunctionWithoutAwait_NonGenericTask()
    {
        var source = @"(module test)
(define-async (do-nothing) : Task 0)";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task DoNothing()", cs);
    }

    [Fact]
    public void AsyncFunctionWithoutAwait_TaskOfString()
    {
        var source = @"(module test)
(define-async (greet) : (Task String) ""hello"")";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task<string> Greet()", cs);
    }

    [Fact]
    public void AsyncClassMethodWithoutAwait_TaskOfInt()
    {
        var source = @"(module test)
(class Worker
  (define-async (DoWork [x : Int]) : (Task Int)
    (+ x 1)))";
        var cs = Compile(source);
        Assert.Contains("sealed class Worker", cs);
        Assert.Contains("async System.Threading.Tasks.Task<int> DoWork(int x)", cs);
    }

    // ─── Class method sibling and module-level calls ─────────────────

    [Fact]
    public void ClassDecl_SiblingMethodCall()
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
    public void ClassDecl_MethodCallsModuleFunction()
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
    public void ClassDecl_RecursiveMethodCall()
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
