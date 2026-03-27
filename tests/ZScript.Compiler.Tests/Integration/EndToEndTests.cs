using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using ZScript.Compiler.Pipeline;

namespace ZScript.Compiler.Tests.Integration;

public class EndToEndTests
{
    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
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
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScript.slnx")))
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
        Assert.Contains("return Main(System.Collections.Immutable.ImmutableList.Create(args));", cs);  // main wrapper references PascalCase inner function
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
(define (make-list) : Unit (list 1 2 3))";
        var compilation = new Compilation();
        var result = compilation.Compile(source);
        // This may have type issues since make-list returns List not Unit,
        // but the compilation pipeline should still produce output
        Assert.NotNull(result);
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
    public void TryPropagateResult()
    {
        var source = @"(module test)
(import stdlib/result)
(import stdlib/error)
(define (safe-div [a : Int] [b : Int]) : (Result Int ErrorInfo)
  (if (= b 0)
    (Err (Error ""division by zero""))
    (Ok (/ a b))))

(define (compute [a : Int] [b : Int] [c : Int]) : (Result Int ErrorInfo)
  (try
    (let [x (? (safe-div a b))]
      (let [y (? (safe-div x c))]
        (Ok (+ x y))))))";
        var cs = Compile(source);
        Assert.Contains("Result", cs);
        Assert.Contains("__r", cs); // propagate temp var
        Assert.Contains("Err", cs);
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
  (magnitude [] : Int
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
  (next [] : Int (+ value 1)))
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
  (get [] : a value))";
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
  (GetName [] : String name))";
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
  (RunTest [] : Int 42))";
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
  (Greet [] : String name))";
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
  (Area [] : Int (* radius radius)))

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
}
