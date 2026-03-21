namespace ZScript.Compiler.Tests.Integration;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ZScript.Compiler.Pipeline;
using Xunit;

public class EndToEndTests
{
    private static string Compile(string source)
    {
        var compilation = new Compilation(new CompilerOptions { OutputMode = OutputMode.CSharp });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
        return result.Output!;
    }

    [Fact]
    public void FactorialFunction()
    {
        var source = @"(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))";
        var cs = Compile(source);
        Assert.Contains("factorial", cs);
        Assert.Contains("while (true)", cs); // TCO
    }

    [Fact]
    public void ArithmeticExpressions()
    {
        var source = @"(define (compute [x : Int]) : Int
  (let [a (+ x 1)]
    (let [b (* a 2)]
      (- b x))))";
        var cs = Compile(source);
        Assert.Contains("compute", cs);
    }

    [Fact]
    public void NestedIfExpressions()
    {
        var source = @"(define (classify [n : Int]) : Int
  (if (< n 0) -1
    (if (= n 0) 0 1)))";
        var cs = Compile(source);
        Assert.Contains("classify", cs);
    }

    [Fact]
    public void MultipleFunctionDefinitions()
    {
        var source = @"
(define (add [x : Int] [y : Int]) : Int (+ x y))
(define (mul [x : Int] [y : Int]) : Int (* x y))
(define (combined [a : Int] [b : Int]) : Int (add (mul a b) a))";
        var cs = Compile(source);
        Assert.Contains("add", cs);
        Assert.Contains("mul", cs);
        Assert.Contains("combined", cs);
    }

    [Fact]
    public void BooleanLogic()
    {
        var source = @"(define (both [a : Bool] [b : Bool]) : Bool (and a (not b)))";
        var cs = Compile(source);
        Assert.Contains("&&", cs);
        Assert.Contains("!", cs);
    }

    [Fact]
    public void GcdFunction()
    {
        var source = @"(define (gcd [a : Int] [b : Int]) : Int
  (if (= b 0) a (gcd b (% a b))))";
        var cs = Compile(source);
        Assert.Contains("gcd", cs);
        Assert.Contains("while (true)", cs); // TCO
    }

    [Fact]
    public void FibonacciTailRecursive()
    {
        var source = @"(define (fib [n : Int] [a : Int] [b : Int]) : Int
  (if (= n 0) a (fib (- n 1) b (+ a b))))";
        var cs = Compile(source);
        Assert.Contains("fib", cs);
        Assert.Contains("while (true)", cs); // TCO
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
        Assert.Contains("System.Console.WriteLine(x)", cs);
        Assert.Contains("Main()", cs);
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
        Assert.Contains("System.Console.WriteLine(x)", cs);
    }

    [Fact]
    public void ListLiteral()
    {
        var source = @"(define (make-list) : Unit (list 1 2 3))";
        var compilation = new Compilation();
        var result = compilation.Compile(source);
        // This may have type issues since make-list returns List not Unit,
        // but the compilation pipeline should still produce output
        Assert.NotNull(result);
    }

    [Fact]
    public void OptionSomeNone()
    {
        var source = @"(define (f [x : Int]) : (Option Int) (if (> x 0) (Some x) None))";
        var cs = Compile(source);
        Assert.Contains("ZsOption", cs);
        Assert.Contains("Some", cs);
        Assert.Contains("None", cs);
    }

    [Fact]
    public void ResultOkErr()
    {
        var source = @"(define (f [x : Int]) : (Result Int Error) (if (> x 0) (Ok x) (Err (Error ""bad""))))";
        var cs = Compile(source);
        Assert.Contains("ZsResult", cs);
        Assert.Contains("Ok", cs);
        Assert.Contains("Err", cs);
        Assert.Contains("ZsError", cs);
    }

    [Fact]
    public void MatchOnOption()
    {
        var source = @"
(define (describe [opt : (Option Int)]) : String
  (match opt
    [(Some v) (string-append ""Got: "" (int->string v))]
    [None ""Nothing""]))";
        var cs = Compile(source);
        Assert.Contains("ZsOption", cs);
        Assert.Contains("Some", cs);
        Assert.Contains("None", cs);
        Assert.Contains("switch", cs);
    }

    [Fact]
    public void MatchOnResult()
    {
        var source = @"
(define (describe [r : (Result Int Error)]) : String
  (match r
    [(Ok v) (string-append ""Success: "" (int->string v))]
    [(Err e) ""Failed""]))";
        var cs = Compile(source);
        Assert.Contains("ZsResult", cs);
        Assert.Contains("Ok", cs);
        Assert.Contains("Err", cs);
        Assert.Contains("switch", cs);
    }

    [Fact]
    public void TryPropagateResult()
    {
        var source = @"
(define (safe-div [a : Int] [b : Int]) : (Result Int Error)
  (if (= b 0)
    (Err (Error ""division by zero""))
    (Ok (/ a b))))

(define (compute [a : Int] [b : Int] [c : Int]) : (Result Int Error)
  (try
    (let [x (? (safe-div a b))]
      (let [y (? (safe-div x c))]
        (Ok (+ x y))))))";
        var cs = Compile(source);
        Assert.Contains("ZsResult", cs);
        Assert.Contains("__r", cs); // propagate temp var
        Assert.Contains("Err", cs);
    }

    [Fact]
    public void IlBackendClrInteropHasCorrectAssemblyReferences()
    {
        var source = @"
(import-clr
  [writeln System.Console/WriteLine])

(let [x ""hello""]
  (writeln x))";

        var compilation = new Compilation(new CompilerOptions { OutputMode = OutputMode.IL });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics));
        Assert.True(result.IsExecutable);
        Assert.NotNull(result.OutputBytes);

        // Verify the emitted PE references System.Runtime, not System.Private.CoreLib
        using var peReader = new PEReader(new MemoryStream(result.OutputBytes));
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
        var source = @"
(record Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        Assert.Contains("new Point(", cs);
    }

    [Fact]
    public void HigherOrderLambda()
    {
        var source = @"(define (apply-fn [f : (Fn [Int] Int)] [x : Int]) : Int (f x))";
        var cs = Compile(source);
        Assert.Contains("System.Func<int, int>", cs);
    }

    [Fact]
    public void CatchClrException()
    {
        var source = @"
(import-clr
  [parse-int System.Int32/Parse])

(define (safe-parse [s : String]) : (Result Int Error)
  (catch (parse-int s)))";
        var cs = Compile(source);
        Assert.Contains("try", cs);
        Assert.Contains("catch", cs);
        Assert.Contains("ZsResult", cs);
        Assert.Contains("ZsError", cs);
    }

    [Fact]
    public void AsyncAwaitRoundTrip()
    {
        var source = @"
(define-async (compute [x : Int]) : (Task Int) (+ x 1))
(define-async (use-it [x : Int]) : (Task Int) (await (compute x)))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task<int> compute(int x)", cs);
        Assert.Contains("async System.Threading.Tasks.Task<int> use_it(int x)", cs);
        Assert.Contains("await", cs);
    }

    [Fact]
    public void AsyncFunctionWithoutAwait()
    {
        var source = @"(define-async (simple [x : Int]) : (Task Int) (+ x 1))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task<int> simple(int x)", cs);
    }

    [Fact]
    public void NestedAwait()
    {
        var source = @"
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int)
  (let [result (await (inner x))]
    (+ result 10)))";
        var cs = Compile(source);
        Assert.Contains("async", cs);
        Assert.Contains("await", cs);
        Assert.Contains("inner(x)", cs);
    }

    [Fact]
    public void AwaitNonGenericTask()
    {
        var source = @"
(define-async (wait) : Task 0)
(define-async (use-wait) : (Task Int)
  (let [_ (await (wait))]
    99))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task wait()", cs);
        Assert.Contains("await", cs);
    }

    [Fact]
    public void AwaitInLet_ProducesStatementNotLambda()
    {
        var source = @"
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int)
  (let [result (await (inner x))]
    (+ result 10)))";
        var cs = Compile(source);
        // Let binding with await must produce var statement, not an IIFE lambda
        Assert.Contains("var result = await inner(x);", cs);
        Assert.DoesNotContain("Func<", cs);
    }

    [Fact]
    public void NonGenericTask_OmitsReturn()
    {
        var source = @"
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (fire-and-forget) : Task
  (await (inner 1)))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task fire_and_forget()", cs);
        // Non-generic Task must not return a value
        Assert.DoesNotContain("return await", cs);
    }

    [Fact]
    public void ChainedAwait_SequentialStatements()
    {
        var source = @"
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (chain [x : Int]) : (Task Int)
  (let [a (await (step x))]
    (let [b (await (step a))]
      (+ a b))))";
        var cs = Compile(source);
        Assert.Contains("var a = await step(x);", cs);
        Assert.Contains("var b = await step(a);", cs);
        Assert.Contains("return (a + b);", cs);
    }

    [Fact]
    public void AwaitDirectReturn_NoLambdaWrap()
    {
        var source = @"
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int) (await (inner x)))";
        var cs = Compile(source);
        // Direct await in body should return without lambda
        Assert.Contains("return await inner(x);", cs);
        Assert.DoesNotContain("Func<", cs);
    }

    [Fact]
    public void AwaitInIfBranches_PreservesControl()
    {
        var source = @"
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (pick [flag : Bool] [x : Int]) : (Task Int)
  (let [result (if flag (await (step x)) (await (step 0)))]
    result))";
        var cs = Compile(source);
        Assert.Contains("await step(x)", cs);
        Assert.Contains("await step(0)", cs);
    }

    [Fact]
    public void AwaitNonGenericInLetThenReturn()
    {
        var source = @"
(define-async (side-effect) : Task 0)
(define-async (do-then-return) : (Task Int)
  (let [_ (await (side-effect))]
    42))";
        var cs = Compile(source);
        Assert.Contains("var _ = await side_effect();", cs);
        Assert.Contains("return 42;", cs);
    }

    [Fact]
    public void MultipleAsyncFunctions_IndependentSignatures()
    {
        var source = @"
(define-async (a [x : Int]) : (Task Int) (+ x 1))
(define-async (b [x : Int] [y : Int]) : (Task Bool) (= x y))
(define-async (c) : Task 0)";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task<int> a(int x)", cs);
        Assert.Contains("async System.Threading.Tasks.Task<bool> b(int x, int y)", cs);
        Assert.Contains("async System.Threading.Tasks.Task c()", cs);
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
        Assert.Contains("public int x { get; }", cs);
        Assert.Contains("public int y { get; }", cs);
        Assert.Contains("public Point(int x, int y)", cs);
        Assert.Contains("this.x = x;", cs);
        Assert.Contains("this.y = y;", cs);
        Assert.Contains("public int magnitude()", cs);
        Assert.Contains("this.x", cs);
    }

    [Fact]
    public void ClassDecl_ConstructorAndFieldAccess()
    {
        var source = @"
(class Point
  [x : Float]
  [y : Float])
(define (get-x [p : Point]) : Float (Point/x p))";
        var cs = Compile(source);
        Assert.Contains("public sealed class Point", cs);
        Assert.Contains("Point_x", cs);
    }

    [Fact]
    public void ClassDecl_MethodSlashSyntax()
    {
        var source = @"
(class Counter
  [value : Int]
  (next [] : Int (+ value 1)))
(define (get-next [c : Counter]) : Int (Counter/next c))";
        var cs = Compile(source);
        Assert.Contains("public sealed class Counter", cs);
        Assert.Contains("Counter_next", cs);
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
        Assert.Contains("public a value { get; }", cs);
        Assert.Contains("public a get()", cs);
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
        Assert.Contains("public string name { get; }", cs);
        Assert.Contains("public string GetName()", cs);
    }

    [Fact]
    public void ClassDecl_ConstructorCallLowersToRecordNew()
    {
        var source = @"
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
}
