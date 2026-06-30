using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

public class EndToEndTests
{
    private static string Compile(string source)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
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

    // Compiles source through the IL backend, loads the emitted assembly, and
    // invokes a zero-arg async method returning Task<int>, returning its result.
    // Used by async-codegen regression tests where the bug produces structurally
    // invalid IL that only manifests at JIT/run time (or under ilverify), so a
    // mere "no diagnostics" assertion would not catch it.
    private static int CompileIlAndAwaitInt(string source, string methodName = "Compute")
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        var task = (System.Threading.Tasks.Task<int>)method.Invoke(null, null)!;
        return task.GetAwaiter().GetResult();
    }

    // Compiles source through the IL backend, loads the emitted assembly, and
    // invokes a zero-arg synchronous method returning int. Used by codegen
    // regression tests where the bug produces a wrong runtime value (not a
    // diagnostic), so only executing the IL catches it.
    private static int CompileIlAndRunInt(string source, string methodName = "Compute")
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        return InvokeUnwrappingInner(method);
    }

    // Compiles source through the C# backend, runs the emitted source through
    // Roslyn into an in-memory assembly, loads it, and invokes a zero-arg method
    // returning int. Used by differential regression tests that need to observe
    // the C# backend's *runtime* behavior (value or thrown exception) rather than
    // just that it compiles — e.g. confirming it agrees with the IL backend on
    // arithmetic that the two emitters could otherwise lower differently.
    private static int CompileCSharpAndRunInt(string source, string methodName = "Compute")
    {
        var cs = Compile(source);

        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        Assert.False(string.IsNullOrEmpty(tpa), "TRUSTED_PLATFORM_ASSEMBLIES unavailable");
        var references = tpa!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p =>
                (Microsoft.CodeAnalysis.MetadataReference)
                    Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)
            )
            .ToList();

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(cs);
        var options = new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
            Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release,
            allowUnsafe: true,
            nullableContextOptions: Microsoft.CodeAnalysis.NullableContextOptions.Enable
        );
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "ZSchemeCSharpExec",
            [tree],
            references,
            options
        );

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(
            emit.Success,
            "Roslyn emit failed:\n"
                + string.Join(
                    "\n",
                    emit.Diagnostics.Where(d =>
                        d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                    )
                )
        );

        var asm = Assembly.Load(ms.ToArray());
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        return InvokeUnwrappingInner(method);
    }

    // Invokes a zero-arg int-returning method, rethrowing the user-program
    // exception unwrapped (reflection wraps it in TargetInvocationException) so
    // Assert.Throws<T> sees the real exception type the backend produced.
    private static int InvokeUnwrappingInner(MethodInfo method)
    {
        try
        {
            return (int)method.Invoke(null, null)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    [Fact]
    public void FactorialFunction()
    {
        var source =
            @"(module test)
(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))";
        var cs = Compile(source);
        Assert.Contains("Factorial", cs);
        Assert.Contains("while (true)", cs); // TCO
    }

    [Fact]
    public void ArithmeticExpressions()
    {
        var source =
            @"(module test)
(define (compute [x : Int]) : Int
  (let ([a (+ x 1)])
    (let ([b (* a 2)])
      (- b x))))";
        var cs = Compile(source);
        Assert.Contains("Compute", cs);
    }

    [Fact]
    public void NestedIfExpressions()
    {
        var source =
            @"(module test)
(define (classify [n : Int]) : Int
  (if (< n 0) -1
    (if (= n 0) 0 1)))";
        var cs = Compile(source);
        Assert.Contains("Classify", cs);
    }

    [Fact]
    public void MultipleFunctionDefinitions()
    {
        var source =
            @"(module test)
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
        var source =
            @"(module test)
(define (both [a : Bool] [b : Bool]) : Bool (and a (not b)))";
        var cs = Compile(source);
        Assert.Contains("&&", cs);
        Assert.Contains("!", cs);
    }

    [Fact]
    public void GcdFunction()
    {
        var source =
            @"(module test)
(define (gcd [a : Int] [b : Int]) : Int
  (if (= b 0) a (gcd b (% a b))))";
        var cs = Compile(source);
        Assert.Contains("Gcd", cs);
        Assert.Contains("while (true)", cs); // TCO
    }

    [Fact]
    public void FibonacciTailRecursive()
    {
        var source =
            @"(module test)
(define (fib [n : Int] [a : Int] [b : Int]) : Int
  (if (= n 0) a (fib (- n 1) b (+ a b))))";
        var cs = Compile(source);
        Assert.Contains("Fib", cs);
        Assert.Contains("while (true)", cs); // TCO
    }

    // Regression: a Unit-returning tail-recursive loop whose base case is the
    // Unit literal `()`. The literal lowers to `default(System.ValueTuple)`,
    // which previously was emitted as a bare statement — illegal C# (CS0201:
    // "Only assignment, call, increment, decrement, await, and new object
    // expressions can be used as a statement"), so Roslyn rejected the output.
    // The base case must instead emit `return;` to exit `while (true)`; without
    // it the loop would also spin forever once it compiled.
    [Fact]
    public void EndToEnd_UnitTailRecursiveLoop_BaseCaseReturnsAndOmitsBareUnit()
    {
        var source =
            @"(module test)
(define (countdown [i : Int]) : Unit
  (if (= i 0) () (countdown (- i 1))))";
        var cs = Compile(source);
        Assert.Contains("while (true)", cs); // TCO
        Assert.Contains("return;", cs); // base case exits the loop
        AssertNoBareUnitStatement(cs);
    }

    // Regression: a Unit-returning function whose entire body is `()`. The body
    // must produce an empty method, not a bare `default(System.ValueTuple);`.
    [Fact]
    public void EndToEnd_UnitLiteralBody_OmitsBareUnitStatement()
    {
        var source =
            @"(module test)
(define (noop) : Unit ())";
        var cs = Compile(source);
        Assert.Contains("public static void Noop()", cs);
        AssertNoBareUnitStatement(cs);
    }

    // Regression: chained Unit literals in a `begin` are lowered through the
    // expression-position let path, which splices statements into a block lambda
    // (`() => { ... }`). The Unit literals must be elided there too, not emitted
    // as bare `default(System.ValueTuple);` statements inside the lambda.
    [Fact]
    public void EndToEnd_BeginWithUnitLiterals_OmitsBareUnitStatement()
    {
        var source =
            @"(module test)
(define (chained [x : Int]) : Unit (begin () ()))";
        var cs = Compile(source);
        Assert.Contains("public static void Chained(int x)", cs);
        AssertNoBareUnitStatement(cs);
    }

    // The Unit literal `default(System.ValueTuple)` is only valid C# when it
    // produces a value (e.g. `return default(System.ValueTuple);` or as an
    // argument), never on its own as a statement. Asserts no occurrence appears
    // in statement position — bare on a line, or following `{`/`;` in a lambda.
    private static void AssertNoBareUnitStatement(string cs)
    {
        const string unit = "default(System.ValueTuple);";
        foreach (var rawLine in cs.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            Assert.False(
                line == unit,
                $"Unit literal emitted as a bare statement (CS0201):\n{rawLine}"
            );
        }

        Assert.DoesNotContain("{ " + unit, cs); // start of a block lambda body
        Assert.DoesNotContain("; " + unit, cs); // after a prior statement
    }

    [Fact]
    public void LetStarBindings()
    {
        var source =
            @"(module test)
(define (compute [x : Int]) : Int
  (let* ([a (+ x 1)] [b (* a 2)] [c (- b x)])
    c))";
        var cs = Compile(source);
        Assert.Contains("Compute", cs);
    }

    [Fact]
    public void ClrInteropLetWithBody()
    {
        var source =
            @"
(import-clr
  [writeln System.Console/WriteLine])

(let ([x ""hello""])
  (writeln x))";
        var cs = Compile(source);
        Assert.Contains("System.Console.WriteLine(X)", cs);
        Assert.Contains("static UnnamedModule()", cs);
        Assert.DoesNotContain("Main()", cs);
    }

    [Fact]
    public void LetWithTypeAnnotationUpcast()
    {
        var source =
            @"(module test)
(let ([s : System.IO.Stream (new System.IO.MemoryStream)])
  s)";
        var cs = Compile(source);
        Assert.Contains("System.IO.Stream", cs);
    }

    [Fact]
    public void ExplicitMainFunction()
    {
        var source =
            @"(module test)
(import-clr
  [writeln System.Console/WriteLine])

(define (main [args : (Clr-Array String)]) : Int
  (begin
    (writeln ""hello"")
    0))";
        var cs = Compile(source);
        // `main` IS the entry point: it is emitted as a single `public static int Main(string[]
        // args)` that Roslyn discovers directly — no forwarding wrapper, no argument conversion.
        // (Clr-Array is the built-in array alias, available with the prelude disabled.)
        Assert.Contains("public static int Main(string[] args)", cs);
        Assert.DoesNotContain("ImmutableList.Create", cs);
        Assert.Equal(1, cs.Split("Main(").Length - 1);
    }

    [Fact]
    public void NoMainFunction_NoEntryPoint()
    {
        var source =
            @"(module test)
(define (add [x : Int] [y : Int]) : Int (+ x y))";
        var cs = Compile(source);
        Assert.DoesNotContain("Main(", cs);
        Assert.DoesNotContain("static TestModule()", cs);
    }

    [Fact]
    public void TopLevelLetWithBody_ProducesStaticConstructor()
    {
        var source =
            @"(module test)
(import-clr
  [writeln System.Console/WriteLine])

(let ([x ""hello""])
  (writeln x))

(define (main [args : (Clr-Array String)]) : Int 0)";
        var cs = Compile(source);
        Assert.Contains("static TestModule()", cs);
        Assert.Contains("Main(string[] args)", cs);
    }

    [Fact]
    public void NamespaceDirective()
    {
        var source =
            @"
(namespace My.App)

(import-clr
  [writeln System.Console/WriteLine])

(let ([x ""hello""])
  (writeln x))";
        var cs = Compile(source);
        Assert.Contains("namespace My.App;", cs);
        Assert.Contains("System.Console.WriteLine(X)", cs);
    }

    [Fact]
    public void ListLiteral()
    {
        var source =
            @"(module test)
(import stdlib/list)
(define (make-list) : (List Int) (list 1 2 3))";
        var cs = Compile(source);
        Assert.NotNull(cs);
    }

    [Fact]
    public void OptionSomeNone()
    {
        var source =
            @"(module test)
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
        var source =
            @"(module test)
(import stdlib/result)
(import stdlib/error)
(define (f [x : Int]) : (Result Int Error) (if (> x 0) (Ok x) (Err (make-error ""bad""))))";
        var cs = Compile(source);
        Assert.Contains("Result", cs);
        Assert.Contains("Ok", cs);
        Assert.Contains("Err", cs);
        Assert.Contains("Error", cs);
    }

    [Fact]
    public void MatchOnOption()
    {
        var source =
            @"(module test)
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
        var source =
            @"(module test)
(import stdlib/result)
(import stdlib/error)
(define (describe [r : (Result Int Error)]) : String
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
        var source =
            @"(module test)
(import-clr
  [writeln System.Console/WriteLine])

(define (main [args : (Mutable-Vector String)]) : Int
  (begin
    (writeln ""hello"")
    0))";

        var compilation = new Compilation(new CompilerOptions { OutputMode = OutputMode.Il });
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
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
        var source = @"(let ([obj (new System.Object)]) obj)";
        var cs = Compile(source);
        Assert.Contains("new System.Object()", cs);
    }

    [Fact]
    public void ClrNew_WithImportClrMethodCall()
    {
        var source =
            @"
(import-clr
  [writeln System.Console/WriteLine])

(let ([obj (new System.Object)])
  (writeln ""constructed""))";
        var cs = Compile(source);
        Assert.Contains("new System.Object()", cs);
        Assert.Contains("System.Console.WriteLine(\"constructed\")", cs);
    }

    [Fact]
    public void RecordConstructorInFunction()
    {
        var source =
            @"(module test)
(define-record Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        Assert.Contains("new Point(", cs);
    }

    [Fact]
    public void HigherOrderLambda()
    {
        var source =
            @"(module test)
(define (apply-fn [f : (Int -> Int)] [x : Int]) : Int (f x))";
        var cs = Compile(source);
        Assert.Contains("System.Func<int, int>", cs);
    }

    [Fact]
    public void CatchClrException()
    {
        var source =
            @"(module test)
(import stdlib/option)
(import stdlib/result)
(import stdlib/error)
(import stdlib/catch)
(import-clr
  [parse-int System.Int32/Parse])

(define (safe-parse [s : String]) : (Result Int Error)
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
        var source =
            @"(module test)
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
        var source =
            @"(module test)
(define-async (simple [x : Int]) : (Task Int) (+ x 1))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task<int> Simple(int x)", cs);
    }

    [Fact]
    public void NestedAwait()
    {
        var source =
            @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int)
  (let ([result (await (inner x))])
    (+ result 10)))";
        var cs = Compile(source);
        Assert.Contains("async", cs);
        Assert.Contains("await", cs);
        Assert.Contains("Inner(x)", cs);
    }

    [Fact]
    public void IlAsync_CapturelessLambdaWithCollidingLet_DoesNotLeakStateMachineContext()
    {
        // Regression: a capture-less lambda created inside an async method's
        // MoveNext was emitted as a static method via EmitFuncDef without clearing
        // the enclosing MoveNext context. If the lambda body contained a `let`
        // whose name collided with a hoisted async local (here `y`, hoisted in
        // `compute`), EmitLet emitted `ldarg.0; stfld <state-machine field>` —
        // but inside the static lambda `ldarg.0` is the lambda's first parameter
        // (an int32), not the state-machine `this`. ilverify rejected this with
        // StackUnexpected (found Int32, expected address of `<Compute>d__N`) and
        // the JIT mis-stored the field, throwing NullReferenceException at run time.
        var source =
            @"(namespace IlAsyncLambdaReg)
(module m)
(define-async (helper [f : (Int -> Int)]) : (Task Int)
  (f 5))
(define-async (compute) : (Task Int)
  (let ([y 10])
    (+ y (await (helper (lambda ([n : Int]) (let ([y (* n 2)]) y)))))))";

        // helper applies the lambda to 5 -> (let y (* 5 2)) -> 10; 10 + 10 = 20.
        Assert.Equal(20, CompileIlAndAwaitInt(source));
    }

    [Fact]
    public void IlAsync_ObjectCtorWithCollidingLet_DoesNotLeakStateMachineContext()
    {
        // Regression (same root cause, object-expression constructor path): an
        // `(object ...)` created directly in an async body had its constructor
        // body (super args + body exprs) emitted without clearing the enclosing
        // MoveNext context. A `let` in the super-args whose name collided with a
        // hoisted async local (`y`) made EmitLet emit `stfld <state-machine field>`
        // against the object's own `this`, which ilverify rejected with
        // StackUnexpected (found ref to the object type, expected address of
        // `<Compute>d__N`). The object's *methods* already cleared the context; the
        // ctor body did not.
        var source =
            @"(namespace IlAsyncObjectReg)
(module m)
(define-class #:open Counter
  [n : Int]
  (define (GetValue) : Int n))
(define-async (helper [x : Int]) : (Task Int)
  x)
(define-async (compute) : (Task Int)
  (let ([y 100])
    (+ (await (helper y))
       (Counter/GetValue (object : Counter
                           (constructor (super (let ([y 7]) (+ y 1)))))))))";

        // helper returns 100; base ctor stores n = (let y 7 (+ y 1)) = 8; 100 + 8 = 108.
        Assert.Equal(108, CompileIlAndAwaitInt(source));
    }

    [Fact]
    public void AwaitNonGenericTask()
    {
        var source =
            @"(module test)
(define-async (wait) : Task 0)
(define-async (use-wait) : (Task Int)
  (let ([_ (await (wait))])
    99))";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task Wait()", cs);
        Assert.Contains("await", cs);
    }

    [Fact]
    public void AwaitInLet_ProducesStatementNotLambda()
    {
        var source =
            @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int)
  (let ([result (await (inner x))])
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
        var source =
            @"(module test)
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
        var source =
            @"(module test)
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (chain [x : Int]) : (Task Int)
  (let ([a (await (step x))])
    (let ([b (await (step a))])
      (+ a b))))";
        var cs = Compile(source);
        Assert.Contains("var a = await Step(x);", cs);
        Assert.Contains("var b = await Step(a);", cs);
        Assert.Contains("return (a + b);", cs);
    }

    [Fact]
    public void AwaitDirectReturn_NoLambdaWrap()
    {
        var source =
            @"(module test)
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
        var source =
            @"(module test)
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (pick [flag : Bool] [x : Int]) : (Task Int)
  (let ([result (if flag (await (step x)) (await (step 0)))])
    result))";
        var cs = Compile(source);
        Assert.Contains("await Step(x)", cs);
        Assert.Contains("await Step(0)", cs);
    }

    [Fact]
    public void AwaitNonGenericInLetThenReturn()
    {
        var source =
            @"(module test)
(define-async (side-effect) : Task 0)
(define-async (do-then-return) : (Task Int)
  (let ([_ (await (side-effect))])
    42))";
        var cs = Compile(source);
        // The let value is `await Task` (Unit-typed in ZScheme, void in C#), so
        // the discard binding emits as a bare statement rather than `_ = ...`.
        Assert.Contains("await SideEffect();", cs);
        Assert.DoesNotContain("_ = await SideEffect();", cs);
        Assert.Contains("return 42;", cs);
    }

    [Fact]
    public void MultipleAsyncFunctions_IndependentSignatures()
    {
        var source =
            @"(module test)
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
        var source =
            @"
(define-class Point
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
        var source =
            @"(module test)
(define-class Point
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
        var source =
            @"(module test)
(define-class Counter
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
        var source =
            @"
(define-class (Container a)
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
        var source =
            @"
(define-class MyService : IDisposable
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
        var source =
            @"(module test)
(define-class Point
  [x : Float]
  [y : Float])
(define (make-point) : Point (Point 1.0 2.0))";
        var cs = Compile(source);
        Assert.Contains("new Point(", cs);
    }

    [Fact]
    public void ClassDecl_MethodsWithAttributes()
    {
        var source =
            @"
(import-clr Xunit)
(define-class MyTests
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
        var source =
            @"
(define-interface IShape
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
        var source =
            @"
(define-interface (IContainer a)
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
        var source =
            @"
(define-interface IDrawable : IShape
  (Draw [] : Unit))";
        var cs = Compile(source);
        Assert.Contains("public interface IDrawable : IShape", cs);
        Assert.Contains("void Draw();", cs);
    }

    [Fact]
    public void InterfaceDecl_ClassImplementsInterface()
    {
        var source =
            @"
(define-interface IGreeter
  (Greet [] : String))

(define-class HelloGreeter : IGreeter
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
        var source =
            @"(module test)
(define-interface IShape
  (Area [] : Int))

(define-class Circle : IShape
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
        var source =
            @"
(@ System.ObsoleteAttribute)
(define-interface ILegacy
  (OldMethod [] : Int))";
        var cs = Compile(source);
        Assert.Contains("[System.ObsoleteAttribute]", cs);
        Assert.Contains("public interface ILegacy", cs);
    }

    [Fact]
    public void InterfaceDecl_MethodWithParameters()
    {
        var source =
            @"
(define-interface ICalculator
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
        var source =
            @"
(define-class #:open Animal
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
        var source =
            @"
(define-class #:open Animal
  [name : String])

(define-class Dog : Animal
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
        var source =
            @"
(define-class #:open Animal
  [name : String]
  (define (Speak) : String name))

(define-class Dog : Animal
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
        var source =
            @"
(define-interface IService
  (Name [] : String))

(define-class #:open BaseService
  [name : String]
  (define (Name) : String name))

(define-class MyService : BaseService IService
  (define (Name) : String
    (string-append ""Service: "" name)))";
        var cs = Compile(source);
        Assert.Contains("public sealed class MyService : BaseService, IService", cs);
    }

    [Fact]
    public void ClassDecl_SuperMethodCall()
    {
        var source =
            @"
(define-class #:open Animal
  [name : String]
  (define (Speak) : String name))

(define-class Dog : Animal
  (define (Speak) : String
    (string-append (super/Speak) ""!"")))";
        var cs = Compile(source);
        Assert.Contains("base.Speak()", cs);
    }

    [Fact]
    public void ClassDecl_ExplicitConstructor()
    {
        var source =
            @"
(define-class #:open Animal
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
        var source =
            @"
(define-class #:open Animal
  [name : String]
  (define (Speak) : String name))

(define-class Dog : Animal
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
    public void ClassDecl_NewCallWithExplicitConstructor_EmitsPositionalArgs()
    {
        // Regression: (new Cls ...) on a class with an explicit constructor
        // whose parameter names differ from the field names used to route
        // through the RecordNew path, which emits named arguments keyed on
        // the field names (e.g., `new FCls_0(F0: 42)`). Since the real ctor
        // parameter was `a0`, Roslyn rejected the C# with CS1739 ("does not
        // have a parameter named 'F0'"). Such classes must use ClrNew
        // (positional) instead.
        var source =
            @"(module test)
(define-class FCls_0
  [f0 : Int #:mutable]
  (constructor [a0 : Int]
    (set! f0 a0))
  (define (get) : Int f0))
(define (compute) : Int (FCls_0/get (new FCls_0 42)))";
        var cs = Compile(source);
        Assert.Contains("new FCls_0(42)", cs);
        Assert.DoesNotContain("F0:", cs);
    }

    [Fact]
    public void ObjectExpr_NestedInsideOuterObjectConstructor_RunsCorrectlyIl()
    {
        // Regression (fuzzer seed 0x31a453b8): an object expression inside
        // the super-args of an outer object expression captures the same
        // outer-scope variable. The outer object's ctor renames the
        // capture to `<name>_param`; the nested call site must use that
        // renamed identifier, not the original. The IL backend already
        // routes through EmitLoadVar at the call site, so this covers the
        // runtime side: if captures ever regress to fetching the wrong
        // slot, the Int value this test threads through will come out
        // wrong (or the method will throw).
        var source =
            @"(module test)
(define-class #:open FCls_0
  [f0 : Int]
  (define (Get) : Int f0))

(define (top [p0 : Int]) : Int
  (let ([outer (object : FCls_0
    (constructor (super (+ (FCls_0/Get (object : FCls_0
                                         (constructor (super p0))))
                           p0))))])
    (FCls_0/Get outer)))

(define (compute) : Int (top 21))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // inner.Get() = 21, then outer.f0 = 21 + 21 = 42, and Get() returns 42.
        Assert.Equal(42, compute.Invoke(null, null));
    }

    [Fact]
    public void GenericJsonSerialize_Primitive_Il()
    {
        // Exercises the IL backend's trailing-optional-parameter support: the bound
        // Serialize<T>(T, JsonSerializerOptions? = null) overload is called with one arg.
        var source =
            @"(module test)
(import-clr
  System.Text.Json
  [json-serialize System.Text.Json.JsonSerializer/Serialize ^a : (^a -> String)])
(define (go) : String (json-serialize 42))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));

        var asm = Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes);
        var go = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Go", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal("42", go.Invoke(null, null));
    }

    [Fact]
    public void GenericJsonSerializeDeserialize_RecordRoundTrip_Il()
    {
        // Exercises a user record as a generic type argument on the IL backend: the value
        // is serialized via Serialize<W> and deserialized back to a real W via Deserialize<W>
        // (proven by reading W/name off the result).
        var source =
            @"(module test)
(import-clr
  System.Text.Json
  [json-serialize System.Text.Json.JsonSerializer/Serialize ^a : (^a -> String)]
  [json-deserialize System.Text.Json.JsonSerializer/Deserialize ^a : (String -> ^a)])
(define-record W [name : String] [count : Int])
(define (roundtrip) : String
  (let ([json (json-serialize (W ""gadget"" 7))])
    (let ([w (json-deserialize json)])
      (W/name w))))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));

        var asm = Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes);
        var roundtrip = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Roundtrip", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal("gadget", roundtrip.Invoke(null, null));
    }

    [Fact]
    public void GenericJsonDeserialize_RecordAnnotatedBinding_RoundTrip_Il()
    {
        // Same as the round-trip above, but the deserialize binding carries an explicit
        // record-type annotation `: W`. The annotation must pin the generic `^a = W` via the
        // record type, not its constructor type. Regression for the aspnet KNOWN_ISSUES
        // entry "A record-type annotation on a generic-return binding infers the constructor
        // type" — previously failed with "'W' vs '(String Int -> W)'".
        var source =
            @"(module test)
(import-clr
  System.Text.Json
  [json-serialize System.Text.Json.JsonSerializer/Serialize ^a : (^a -> String)]
  [json-deserialize System.Text.Json.JsonSerializer/Deserialize ^a : (String -> ^a)])
(define-record W [name : String] [count : Int])
(define (roundtrip) : String
  (let ([json (json-serialize (W ""gadget"" 7))])
    (let ([w : W (json-deserialize json)])
      (W/name w))))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));

        var asm = Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes);
        var roundtrip = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Roundtrip", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal("gadget", roundtrip.Invoke(null, null));
    }

    [Fact]
    public void ClassDecl_NewCallWithExplicitConstructor_RunsCorrectlyIl()
    {
        var source =
            @"(module test)
(define-class FCls_0
  [f0 : Int #:mutable]
  (constructor [a0 : Int]
    (set! f0 a0))
  (define (get) : Int f0))
(define (compute) : Int (FCls_0/get (new FCls_0 42)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(42, compute.Invoke(null, null));
    }

    [Fact]
    public void ObjectExpr_InsideLambda_CapturesOuterFuncParam_RunsCorrectlyIl()
    {
        // Regression (fuzzer seed 0xf9554406): an object expression inside a
        // lambda that captures a parameter from the enclosing function used
        // to fail IL emission with ``Variable 'x0' not found''. The IL
        // closure converter's FindFreeVars didn't descend into `ObjectExpr`,
        // so the outer function's parameter was never added to the lifted
        // lambda's capture list. When the object constructor tried to read
        // that parameter at the `newobj` call site, EmitLoadVar couldn't
        // find it in locals, outerParams, class fields, or static fields.
        //
        // The fix teaches FindFreeVars about ObjectExpr — recursing into
        // each method body (bound by the method's params) and into the
        // constructor's super args, field sets, and body exprs (bound by
        // the ctor's params). The lambda now correctly captures outer
        // parameters referenced from any of those positions.
        var source =
            @"(module test)
(define-class #:open Animal
  [name : Int]
  (define (Speak) : Int name))

(define (make-closure [x0 : Int]) : (Int -> Animal)
  (lambda ([x1 : Int])
    (object : Animal
      (constructor (super (+ x0 x1))))))

(define (compute) : Int
  (Animal/Speak ((make-closure 10) 20)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(30, compute.Invoke(null, null));
    }

    [Fact]
    public void ObjectExpr_InsideLambda_MethodBodyReadsOuterParam_RunsCorrectlyIl()
    {
        // Companion to the constructor-capture case above: the free var
        // lives in an object method body rather than in the super args.
        // FindFreeVars must recurse into ObjectExpr.Methods too, otherwise
        // the lifted lambda doesn't capture the outer parameter and
        // EmitObjectExpr's capture collection silently drops it when the
        // method body later tries to load it.
        var source =
            @"(module test)
(define-interface IThunk
  (Call  : Int))

(define (make-closure [x0 : Int]) : (Int -> IThunk)
  (lambda ([x1 : Int])
    (object IThunk
      (define (Call) : Int (+ x0 x1)))))

(define (compute) : Int
  (IThunk/Call ((make-closure 100) 5)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(105, compute.Invoke(null, null));
    }

    [Fact]
    public void ObjectExpr_InsideLambdaInsideClassMethod_ReadsClassField_RunsCorrectlyIl()
    {
        // Regression (fuzzer seed 0xc45858ca, several cases incl. 0x99cd3493,
        // 0x0ae14aa4, 0xdbfb2194, 0xdd19ed7f): an `object` expression nested
        // inside a lambda inside a class instance method, where the object's
        // method body reads a class field, produced IL that failed verification
        // with ``Unrecognized local variable number'' at offset 0 of the
        // anonymous-class method.
        //
        // The lambda lifts to a closure class and captures the enclosing
        // class's `this` as a synthetic local — EmitLambda saves that local
        // into `_currentClassThisLocal` so subsequent class-field accesses
        // inside the lambda body resolve through it. EmitObjectExpr later runs
        // for the inner object, captures the field into the anonymous class as
        // a real field, and emits methods on that anonymous class. But when
        // emitting those methods it forgot to clear `_currentClassThisLocal` /
        // `_moveNextCtx`, so a class-field read inside the object's method
        // routed through `EmitLoadClassThis`'s `ldloc thisLocal` — referencing
        // the lambda's local from a different method body. The verifier
        // rejected the resulting IL.
        //
        // The fix saves and nulls `_currentClassThisLocal` and `_moveNextCtx`
        // around the object-method body emission so `EmitLoadClassThis` falls
        // back to `ldarg.0`, which is correctly the object's own `this`.
        var source =
            @"(module test)
(define-interface IThunk
  (Call  : Int))

(define-class FCls_0
  [f1 : Int #:mutable]
  (define (Run) : Int
    ((lambda ([x : Int])
       (let ([obj : IThunk (object IThunk
                            (define (Call) : Int (+ f1 x)))])
         (IThunk/Call obj))) 7)))

(define (compute) : Int
  (FCls_0/Run (new FCls_0 35)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(42, compute.Invoke(null, null));
    }

    [Fact]
    public void ObjectExpr_MethodInvokesCapturedDelegateParam_RunsCorrectlyIl()
    {
        // Regression (fuzzer seed 0xa86c7c76, case 0xab34b09e): an `object`
        // expression's method that calls a delegate-typed parameter captured
        // from the enclosing `define` failed IL emission with
        // ``Function 'f' not found for AsmResolver IL emission''. The
        // capture analysis correctly threaded the delegate through as a
        // class field, but EmitCall's resolver only consulted methods,
        // locals, outerParams, _staticFields, and sibling class methods —
        // captured class fields were never checked, so calling the captured
        // delegate by name fell through to the error path.
        //
        // The fix adds a `_currentClassFields` lookup in EmitCall (mirroring
        // EmitLoadVar's order) so a delegate-typed capture is loaded via
        // `this.<field>` and invoked. This test exercises the runtime path:
        // if the resolution ever regresses to a stub, the value coming back
        // will be wrong rather than throwing.
        var source =
            @"(module test)
(define-interface IFoo
  (Call  : Int))

(define (make-obj [f : (Int -> Int)] [x : Int]) : IFoo
  (object IFoo
    (define (Call) : Int (f x))))

(define (compute) : Int
  (IFoo/Call (make-obj (lambda ([n : Int]) (* n 2)) 21)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(42, compute.Invoke(null, null));
    }

    [Fact]
    public void ObjectExpr_SuperArgInvokesCapturedFuncTypedParam_RunsCorrectlyIl()
    {
        // Regression (fuzzer seed 0x8e242ca4): an object expression whose
        // super-args invoke a function-typed parameter captured from the
        // enclosing function used to crash IL emission with
        // ArgumentOutOfRangeException at Parameters[i + _instanceArgOffset].
        //
        // EmitObjectExpr threads captures through as ctor parameters and
        // builds a synthesized outerParams list mirroring them. While
        // emitting the super args, _instanceArgOffset is set to 1 (the ctor
        // is an instance method). EmitCall's "delegate-typed parameter
        // invocation" path was indexing method.Parameters with
        // (i + _instanceArgOffset), but AsmResolver's Parameters collection
        // already excludes `this` — so the offset over-shoots by one and
        // either loaded the wrong parameter or threw when the captured
        // delegate was the last (or only) ctor parameter.
        //
        // The fix drops the offset from that path, matching EmitLoadVar's
        // existing comment that Parameters is 0-indexed regardless of
        // static/instance.
        var source =
            @"(module test)
(define-class #:open Base
  [f0 : Int #:mutable]
  (define (M [p : Int]) : Int p))

(define (run [g : (Int -> Int)]) : Int
  (let ([obj (object : Base
    (constructor (super (g 7)))
    (define (M [p : Int]) : Int p))])
    (Base/f0 obj)))

(define (compute) : Int
  (run (lambda ([n : Int]) (+ n 35))))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(42, compute.Invoke(null, null));
    }

    [Fact]
    public void ObjectExpr_SuperArgInvokesFuncTypedParamCapturedThroughLambda_RunsCorrectlyIl()
    {
        // Regression (fuzzer seed 0xcca307c7): an object expression whose
        // super-args invoke a function-typed value that reaches the object
        // through a lifted lambda's closure used to fail IL emission with
        // ``Function 'f' not found for AsmResolver IL emission''.
        //
        // Pipeline: `run` has a delegate-typed param `f`. An inner `(lambda (m) ...)`
        // captures `f` and is closure-converted into a hoisted lambda whose
        // Invoke method loads `f` from a closure field into a local at entry.
        // Inside the lambda body, an `(object : Box (constructor (super (f m))))`
        // expression's super-args reference `f`. EmitObjectExpr collected `f`
        // as a free var, found it in the lambda's `locals` map, and built a
        // capture entry for it — but with `ZType.Unit` as a placeholder
        // because locals don't carry ZType. The synthesized ctor outerParams
        // list therefore had `f : Unit`, and EmitCall's delegate-parameter
        // path requires `ZType.ZFuncType`, so the call site fell through to
        // the unresolved-function error.
        //
        // The fix recovers the original ZType by walking the object expr's
        // IR for any `IrNode.Var` with the same name (those nodes carry the
        // type information). The recovered type also flows to the inner
        // class's capture entry, which preserves delegate detection if the
        // capture is re-captured by a deeper nested object/lambda.
        var source =
            @"(module test)
(define-class #:open Box [v : Int #:mutable])

(define (run [f : (Int -> Int)]) : Int
  ((lambda ([m : Int])
    (let ([b (object : Box
              (constructor (super (f m))))])
      (Box/v b)))
   7))

(define (compute) : Int
  (run (lambda ([x : Int]) (+ x 35))))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(42, compute.Invoke(null, null));
    }

    [Fact]
    public void ClassDecl_NewCallWithExplicitConstructorAndSuper_EmitsPositionalArgs()
    {
        // Inheritance variant: the subclass has an explicit constructor that
        // forwards to super. Field-name named arguments would not match the
        // sub-ctor's single-param signature either.
        var source =
            @"
(define-class #:open Base
  [b : Int])
(define-class Derived : Base
  [d : Int #:mutable]
  (constructor [a0 : Int]
    (super a0)
    (set! d a0))
  (define (get) : Int d))
(define (compute) : Int (Derived/get (new Derived 7)))";
        var cs = Compile(source);
        Assert.Contains("new Derived(7)", cs);
        Assert.DoesNotContain("D:", cs);
    }

    [Fact]
    public void ImportClr_InstanceMethod()
    {
        var source =
            @"(module test)
(import-clr
  [str-length System.String.Length :instance-property : (String -> Int)]
  [str-substring System.String.Substring :instance : (String Int Int -> String)])

(define (get-len [s : String]) : Int (str-length s))
(define (get-sub [s : String] [start : Int] [len : Int]) : String (str-substring s start len))";
        var cs = Compile(source);
        Assert.Contains("s.Length", cs);
        Assert.Contains("s.Substring(", cs);
    }

    [Fact]
    public void ImportClr_InstanceProperty()
    {
        var source =
            @"(module test)
(import-clr
  [list-count System.Collections.Immutable.ImmutableList.Count :instance-property : ((List ^a) -> Int)])

(define (count-items [xs : (List Int)]) : Int (list-count xs))";
        var cs = Compile(source);
        Assert.Contains(".Count", cs);
    }

    [Fact]
    public void ImportClr_InstanceIndexer()
    {
        var source =
            @"(module test)
(import-clr
  [list-item System.Collections.Immutable.ImmutableList.Item :instance-indexer : ((List ^a) Int -> ^a)])

(define (get-first [xs : (List Int)]) : Int (list-item xs 0))";
        var cs = Compile(source);
        Assert.Contains("[0]", cs);
    }

    [Fact]
    public void ImportClr_InstanceIndexer_OnString_IlBackend()
    {
        // Regression: System.String's indexer is named `Chars`, not `Item`. The IL
        // emitter previously hard-coded `get_Item` and so failed with
        // "Indexer not found on System.String". The C# emitter is unaffected because
        // it generates `s[i]` and lets Roslyn resolve the member.
        // Reproduced by the fuzzer with seed 0xb0878680.
        var source =
            @"(module test)
(import-clr
  [str-char System.String.Item :instance-indexer : (String Int -> Char)]
  [char-int System.Convert/ToInt32 : (Char -> Int)])

(define (compute) : Int (char-int (str-char ""AB"" 0)))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "IL compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
    }

    [Fact]
    public void ImportClr_SubtypePassedAsSupertype()
    {
        var source =
            @"(module test)
(import-clr
  [stream-length System.IO.Stream.Length
    :instance-property : (System.IO.Stream -> Long)])

(define (get-length [s : System.IO.Stream]) : Long
  (stream-length s))

(define (test) : Long
  (get-length (new System.IO.MemoryStream)))";
        var cs = Compile(source);
        Assert.Contains(".Length", cs);
    }

    [Fact]
    public void ImportClr_InstancePropertySet()
    {
        var source =
            @"(module test)
(import-clr
  [set-base-addr System.Net.Http.HttpRequestMessage.Content
    :instance-property-set : (System.Net.Http.HttpRequestMessage System.Net.Http.HttpContent -> Unit)])

(define (set-content [msg : System.Net.Http.HttpRequestMessage] [c : System.Net.Http.HttpContent]) : Unit
  (set-base-addr msg c))";
        var cs = Compile(source);
        Assert.Contains(".Content = ", cs);
    }

    [Fact]
    public void ImportClr_InstancePropertyInit()
    {
        var source =
            @"(module test)
(import-clr
  [set-base-addr System.Net.Http.HttpRequestMessage.Content
    :instance-property-init : (System.Net.Http.HttpRequestMessage System.Net.Http.HttpContent -> Unit)])

(define (set-content [msg : System.Net.Http.HttpRequestMessage] [c : System.Net.Http.HttpContent]) : Unit
  (set-base-addr msg c))";
        var cs = Compile(source);
        Assert.Contains(".Content = ", cs);
    }

    [Fact]
    public void ClassDecl_InitFields_HaveInitAccessors()
    {
        var source =
            @"(module test)
(define-class Config
  [host : String #:init]
  [port : Int #:init])";
        var cs = Compile(source);
        Assert.Contains("public string Host { get; init; }", cs);
        Assert.Contains("public int Port { get; init; }", cs);
    }

    [Fact]
    public void ClassDecl_MutableFields_HaveSetAccessors()
    {
        var source =
            @"(module test)
(define-class Counter
  [count : Int #:mutable]
  (define (Increment) : Unit
    (set! count (+ count 1))))";
        var cs = Compile(source);
        Assert.Contains("public int Count { get; set; }", cs);
    }

    [Fact]
    public void MutableVectorToVector_Conversion()
    {
        var source =
            @"(module test)
(import stdlib/vector)
(define (test [arr : (Mutable-Vector Int)]) : (Vector Int)
  (vector->immutable-vector arr))";
        var cs = Compile(source);
        Assert.Contains("ImmutableArray.Create<T0>(", cs);
    }

    [Fact]
    public void VectorToMutableVector_Conversion()
    {
        var source =
            @"(module test)
(import stdlib/mutable/vector)
(define (test [a : (Vector Int)]) : (Mutable-Vector Int)
  (vector->mutable-vector a))";
        var cs = Compile(source);
        Assert.Contains("System.Linq.Enumerable.ToArray<T0>(", cs);
    }

    [Fact]
    public void MutableTreeListToTreeList_Conversion()
    {
        var source =
            @"(module test)
(import stdlib/treelist)
(define (test [ml : (Mutable-TreeList Int)]) : (TreeList Int)
  (mutable-treelist-snapshot ml))";
        var cs = Compile(source);
        Assert.Contains("ImmutableList.CreateRange<T0>(", cs);
    }

    [Fact]
    public void TreeListToMutableTreeList_Conversion()
    {
        var source =
            @"(module test)
(import stdlib/mutable/treelist)
(define (test [l : (TreeList Int)]) : (Mutable-TreeList Int)
  (treelist-copy l))";
        var cs = Compile(source);
        Assert.Contains("System.Linq.Enumerable.ToList<T0>(", cs);
    }

    [Fact]
    public void MutableHashToHash_Conversion()
    {
        var source =
            @"(module test)
(import stdlib/hash)
(define (test [mm : (Mutable-Hash String Int)]) : (Hash String Int)
  (mutable-hash->hash mm))";
        var cs = Compile(source);
        Assert.Contains("ImmutableDictionary.CreateRange<T0, T1>(", cs);
    }

    [Fact]
    public void HashCopy_Conversion()
    {
        var source =
            @"(module test)
(import stdlib/mutable/hash)
(define (test [m : (Hash String Int)]) : (Mutable-Hash String Int)
  (hash-copy m))";
        var cs = Compile(source);
        // Regression: the lowering used to emit `new Dictionary(...)` without
        // any generic arguments, which is invalid C# (CS0305). `hash-copy`
        // is now an ordinary stdlib function, so the regression splits in two:
        // the stdlib body must construct the Dictionary with its own generic
        // type params, and the call site must pass concrete type args through.
        Assert.Contains("new System.Collections.Generic.Dictionary<T0, T1>(", cs);
        Assert.Contains("HashCopy<string, int>(", cs);
    }

    [Fact]
    public void HashCopy_Conversion_LiteralHash()
    {
        // Found by the fuzzer: (hash-copy (hash ...)) inside a let/begin
        // chain emitted `new Dictionary(...)` without generic args, causing
        // CS0305 ("Using the generic type 'Dictionary<TKey, TValue>' requires
        // 2 type arguments"). The literal hash expression supplies the K/V
        // types via inference rather than an annotation — verify they reach
        // the call site as concrete <string, int> args.
        var source =
            @"(module test)
(import stdlib/hash)
(import stdlib/mutable/hash)
(define (test) : Int
  (let ([m (hash-copy (hash (pair ""a"" 1) (pair ""b"" 2)))])
    (hash-count m)))";
        var cs = Compile(source);
        Assert.Contains("new System.Collections.Generic.Dictionary<T0, T1>(", cs);
        Assert.Contains("HashCopy<string, int>(", cs);
    }

    // ─── Generic new ─────────────────────────────────────────────────

    [Fact]
    public void GenericNew_Dictionary()
    {
        var source =
            @"(module test)
(import stdlib/mutable/hash)
(define (make-dict) : (Mutable-Hash String Int)
  (new (System.Collections.Generic.Dictionary String Int)))";
        var cs = Compile(source);
        Assert.Contains("new System.Collections.Generic.Dictionary<string, int>()", cs);
    }

    [Fact]
    public void GenericNew_List()
    {
        var source =
            @"(module test)
(import stdlib/mutable/treelist)
(define (make-list) : (Mutable-TreeList Int)
  (new (System.Collections.Generic.List Int)))";
        var cs = Compile(source);
        Assert.Contains("new System.Collections.Generic.List<int>()", cs);
    }

    // ─── Out parameter support ───────────────────────────────────────

    [Fact]
    public void OutParam_IntTryParse()
    {
        var source =
            @"(module test)
(import-clr
  [try-parse System.Int32/TryParse])
(define (test [s : String]) : (ValueTuple Bool Int)
  (try-parse s))";
        var cs = Compile(source);
        Assert.Contains("out", cs);
        Assert.Contains("TryParse", cs);
    }

    // Regression: when a static `import-clr` is given an explicit annotated type
    // (the visible-out-stripped signature), out-parameter detection used to be
    // gated on `Kind == Instance`. As a result both backends emitted a call to a
    // non-existent `TryParse(string)` overload — the IL backend errored
    // ("CLR method 'System.Int32.TryParse' not found"); the C# backend silently
    // produced code Roslyn would reject. Found by the differential fuzzer.
    [Fact]
    public void OutParam_AnnotatedStaticImport_CSharpEmitsOutCall()
    {
        var source =
            @"(module test)
(import-clr
  [try-parse System.Int32/TryParse : (String -> (ValueTuple Bool Int))])
(define (test [s : String]) : Int
  (value/1 (try-parse s)))";
        var cs = Compile(source);
        Assert.Contains("out", cs);
        Assert.Contains("TryParse", cs);
    }

    [Fact]
    public void OutParam_AnnotatedStaticImport_IlBackendCompiles()
    {
        var source =
            @"(module test)
(import-clr
  [try-parse System.Int32/TryParse : (String -> (ValueTuple Bool Int))])
(define (test [s : String]) : Int
  (value/1 (try-parse s)))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "IL backend rejected annotated static out-param import:\n"
                + string.Join("\n", result.Diagnostics.Diagnostics)
        );
    }

    // Regression: when an annotated `import-clr` targets a method whose CLR type
    // exposes both a no-out-param overload and an out-param overload (e.g.
    // `Dictionary<,>.Remove(TKey)` vs `Remove(TKey, out TValue)`),
    // `PickBestOverload` always preferred the out-param overload. Combined with
    // the recently added annotation-aware out-param detection, that caused
    // out-param metadata to be registered even though the annotation declared a
    // plain (non-tuple) return. The C# emitter then produced a `Func<bool>`
    // whose body returned a tuple (Roslyn rejected it with CS1503) and IL
    // verification flagged a stack-mismatch in `MutableHash_Remove_b`. Found by
    // the differential fuzzer (seed 0x6d1c6eb4) compiling `stdlib/mutable/hash`.
    [Fact]
    public void OutParam_AnnotatedInstanceImport_NonTupleReturn_NoOutParamCall()
    {
        var source =
            @"(module test)
(import-clr
  System.Collections.Generic
  [dict-remove System.Collections.Generic.Dictionary.Remove
    :instance : ((Mutable-Hash ^k ^v) ^k -> Bool)])
(define (drop [m : (Mutable-Hash ^k ^v)] [k : ^k]) : Bool
  :where (^k notnull)
  (dict-remove m k))";
        var cs = Compile(source);
        Assert.Contains("Remove(", cs);
        Assert.DoesNotContain("out __out", cs);
    }

    [Fact]
    public void OutParam_AnnotatedInstanceImport_NonTupleReturn_IlBackendCompiles()
    {
        var source =
            @"(module test)
(define-type-alias (Mutable-Hash ^k ^v) System.Collections.Generic.Dictionary)
(import-clr
  System.Collections.Generic
  [dict-remove System.Collections.Generic.Dictionary.Remove
    :instance : ((Mutable-Hash ^k ^v) ^k -> Bool)])
(define (drop [m : (Mutable-Hash ^k ^v)] [k : ^k]) : Bool
  :where (^k notnull)
  (dict-remove m k))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "IL backend rejected annotated instance import with non-tuple return:\n"
                + string.Join("\n", result.Diagnostics.Diagnostics)
        );
    }

    // ─── set! in method bodies ──────────────────────────────────────

    [Fact]
    public void SetField_MutableFieldInMethodBody()
    {
        var source =
            @"
(define-class Counter
  [count : Int #:mutable]
  (define (Increment) : Unit
    (set! count (+ count 1))))";
        var cs = Compile(source);
        Assert.Contains("this.Count = (this.Count + 1)", cs);
    }

    [Fact]
    public void SetField_MutableFieldInBeginBlock()
    {
        var source =
            @"
(define-class Counter
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
        var source =
            @"
(define-class Foo
  [name : String]
  (define (SetName [n : String]) : Unit
    (set! name n)))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("Cannot set! immutable field")
        );
    }

    [Fact]
    public void SetField_UnknownFieldErrors()
    {
        var source =
            @"
(define-class Foo
  [name : String]
  (define (SetName [n : String]) : Unit
    (set! unknown n)))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Diagnostics, d => d.Message.Contains("Unknown field"));
    }

    [Fact]
    public void BeginInsideTcoLoop_Il()
    {
        // Regression: `(begin e1 e2 ... en)` desugars to nested `(let ([_ ei]) ...)`.
        // Inside a tail-recursive function, emitting these as `var _ = ei;` at
        // statement level produced invalid C# (CS0128 — `_` already defined) and
        // the fuzzer found it via the diffexec oracle. IL is unaffected because
        // locals are slot-indexed, so this test exists to pin the runtime
        // behavior of `begin` in a TCO branch: the intermediate expressions
        // must still be evaluated and their results discarded, with the final
        // expression becoming the return value.
        var source =
            @"(module test)
(define (go [x : Int]) : Int
  (if (<= x 0)
      (begin 111 222 x)
      (go (- x 1))))

(define (compute) : Int
  (go 3))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(0, compute.Invoke(null, null));
    }

    [Fact]
    public void ClrNew_UserDefinedClass_Il()
    {
        // (new FCls_0 ...) for a user-defined ZScheme class used to fail IL
        // emission with "CLR type 'FCls_0' not found" because EmitClrNew only
        // consulted CLR reflection, which can't see types we're currently
        // emitting. The emitter now also checks _userTypes so same-module
        // class constructors resolve cleanly.
        var source =
            @"(module test)
(define-class FCls_0
  [f0 : Int #:mutable]
  (constructor [a0 : Int]
    (set! f0 a0)))

(define (compute) : Int
  (begin (new FCls_0 42) 0))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(0, compute.Invoke(null, null));
    }

    [Fact]
    public void PrecompiledRecord_FieldAccess_Il()
    {
        // Regression (fuzzer): reading a field of a record that lives in a
        // *precompiled* (DLL) dependency. The accessor `Point/x` lowers to a
        // property MethodCall carrying the ZScheme field name "x", but the
        // precompiled CLR property is PascalCase "X". The IL emitter's
        // reflection-based property lookup used the raw, unsanitized name, found
        // nothing, and emitted a `ldc.i4.0` stub instead of the getter — which
        // left the receiver on the stack and produced invalid IL (a stack
        // imbalance / verification failure). It must sanitize the field name to
        // match the precompiled property, exactly as the in-module path does.
        var (dllPath, cleanup) = BuildPrecompiledRecordPackage();
        try
        {
            var source =
                @"(module test)
(import geom)
(define (compute) : Int
  (Point/x (make-point 7 9)))";

            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.Il,
                    AllowsImplicitModuleName = true,
                    DisablePrelude = true,
                    PrecompiledPackagePaths = { dllPath },
                }
            );
            var result = compilation.Compile(source);
            Assert.True(
                result.Success,
                "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
            );

            var ilResult = (CompilationResult.IlOutputResult)result;
            var asm = Assembly.Load(ilResult.OutputBytes);
            var compute = asm.GetExportedTypes()
                .SelectMany(t => t.GetMethods())
                .First(m =>
                    m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                    && m.GetParameters().Length == 0
                );
            Assert.Equal(7, compute.Invoke(null, null));
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    ///     Compiles a tiny "geom" package (a record plus a constructor function)
    ///     to a real precompiled DLL + metadata sidecar on disk, so consuming
    ///     compilations resolve its record type through the reflection-based
    ///     precompiled path rather than as an in-module TypeDefinition. Returns
    ///     the DLL path and a cleanup callback.
    /// </summary>
    private static (string DllPath, Action Cleanup) BuildPrecompiledRecordPackage()
    {
        var pkgSrc = Path.Combine(Path.GetTempPath(), $"zs_pkgsrc_{Guid.NewGuid():N}");
        var pkgOut = Path.Combine(Path.GetTempPath(), $"zs_pkgout_{Guid.NewGuid():N}");
        Directory.CreateDirectory(pkgSrc);
        Directory.CreateDirectory(pkgOut);

        File.WriteAllText(
            Path.Combine(pkgSrc, "geom.zs"),
            "(module geom)\n"
                + "(export Point make-point)\n"
                + "(define-record Point [x : Int] [y : Int])\n"
                + "(define (make-point [a : Int] [b : Int]) : Point (Point a b))"
        );

        var manifest = new PackageManifest(
            "geom",
            "0.1.0",
            null,
            "geom",
            "geom",
            null,
            null,
            new PackageDependencies([], []),
            new PackageDependencies([], []),
            new BuildConfig(new MainBuildConfig(null, null, "Geom.Pkg", []), null),
            null,
            SourceSpan.None
        );

        var diag = new DiagnosticBag();
        var libResult = new LibraryCompiler(diag).Compile(
            pkgSrc,
            manifest,
            new CompilerOptions { OutputMode = OutputMode.Il, DisablePrelude = true }
        );
        Assert.True(
            libResult is not null && !diag.HasErrors,
            "Package compilation failed:\n" + string.Join("\n", diag.Diagnostics)
        );

        var dllPath = Path.Combine(pkgOut, "geom.dll");
        File.WriteAllBytes(dllPath, libResult!.AssemblyBytes);
        File.WriteAllText(
            Path.ChangeExtension(dllPath, ".metadata.json"),
            MetadataSerializer.Serialize("geom", "0.1.0", "geom", libResult.Modules, "geom", "geom")
        );

        return (
            dllPath,
            () =>
            {
                try
                {
                    Directory.Delete(pkgSrc, true);
                }
                catch
                {
                    // best-effort cleanup
                }

                try
                {
                    Directory.Delete(pkgOut, true);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        );
    }

    [Fact]
    public void PrecompiledCollidingExports_ConsumerResolvesRenamedSymbol_Il()
    {
        // A precompiled library exports two functions whose sanitized names collide
        // (`this-function`/`ThisFunction`); the library disambiguates the second to
        // `ThisFunction_fn` and persists the rename in its metadata. A consumer must
        // read that map and reference the renamed symbol by the name in the DLL.
        var (dllPath, cleanup) = BuildPrecompiledCollisionPackage();
        try
        {
            var source =
                @"(module test)
(import coll)
(define (compute) : Int (- (this-function) (ThisFunction)))";

            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.Il,
                    AllowsImplicitModuleName = true,
                    DisablePrelude = true,
                    PrecompiledPackagePaths = { dllPath },
                }
            );
            var result = compilation.Compile(source);
            Assert.True(
                result.Success,
                "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
            );

            var ilResult = (CompilationResult.IlOutputResult)result;
            var asm = Assembly.Load(ilResult.OutputBytes);
            var compute = asm.GetExportedTypes()
                .SelectMany(t => t.GetMethods())
                .First(m =>
                    m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                    && m.GetParameters().Length == 0
                );
            Assert.Equal(3, compute.Invoke(null, null));
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    ///     Compiles a tiny "coll" package whose two exported functions sanitize to the
    ///     same identifier, forcing EmitNameResolver to rename one and persist the rename
    ///     into metadata. Returns the DLL path and a cleanup callback.
    /// </summary>
    private static (string DllPath, Action Cleanup) BuildPrecompiledCollisionPackage()
    {
        var pkgSrc = Path.Combine(Path.GetTempPath(), $"zs_pkgsrc_{Guid.NewGuid():N}");
        var pkgOut = Path.Combine(Path.GetTempPath(), $"zs_pkgout_{Guid.NewGuid():N}");
        Directory.CreateDirectory(pkgSrc);
        Directory.CreateDirectory(pkgOut);

        File.WriteAllText(
            Path.Combine(pkgSrc, "coll.zs"),
            "(module coll)\n"
                + "(export this-function ThisFunction)\n"
                + "(define (this-function) : Int 10)\n"
                + "(define (ThisFunction) : Int 7)"
        );

        var manifest = new PackageManifest(
            "coll",
            "0.1.0",
            null,
            "coll",
            "coll",
            null,
            null,
            new PackageDependencies([], []),
            new PackageDependencies([], []),
            new BuildConfig(new MainBuildConfig(null, null, "Coll.Pkg", []), null),
            null,
            SourceSpan.None
        );

        var diag = new DiagnosticBag();
        var libResult = new LibraryCompiler(diag).Compile(
            pkgSrc,
            manifest,
            new CompilerOptions { OutputMode = OutputMode.Il, DisablePrelude = true }
        );
        Assert.True(
            libResult is not null && !diag.HasErrors,
            "Package compilation failed:\n" + string.Join("\n", diag.Diagnostics)
        );

        // The rename must have been recorded for the colliding exported symbol.
        var collModule = libResult!.Modules.Values.First(m =>
            m.ExportedNames.Contains("ThisFunction")
        );
        Assert.Equal("ThisFunction_fn", collModule.EmittedNames!["ThisFunction"]);

        var dllPath = Path.Combine(pkgOut, "coll.dll");
        File.WriteAllBytes(dllPath, libResult.AssemblyBytes);
        File.WriteAllText(
            Path.ChangeExtension(dllPath, ".metadata.json"),
            MetadataSerializer.Serialize("coll", "0.1.0", "coll", libResult.Modules, "coll", "coll")
        );

        return (
            dllPath,
            () =>
            {
                try
                {
                    Directory.Delete(pkgSrc, true);
                }
                catch
                {
                    // best-effort cleanup
                }

                try
                {
                    Directory.Delete(pkgOut, true);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        );
    }

    [Fact]
    public void PrecompiledCollidingTypes_ConsumerResolvesRenamedType_BothBackends()
    {
        // A precompiled library exports two record types whose sanitized names collide
        // (`r`/`R`); the library disambiguates the second to `R_type` and persists the
        // rename in its metadata (asserted in the package builder). A consumer must then
        // reference the renamed type by the name baked into the DLL — the C# backend by
        // qualifying with the persisted emitted name, the IL backend by aliasing its
        // imported-type registry so construction resolves to the baked type.
        var (dllPath, moduleName, cleanup) = BuildPrecompiledTypeCollisionPackage();
        try
        {
            // C# backend: full construct + field access; the renamed precompiled type is
            // referenced by its baked name (`R_type`) and the program compiles.
            var fieldSource =
                $@"(module test)
(import {moduleName})
(define (compute) : Int (- (R/b (R 10)) (r/a (r 7))))";
            var csResult = (CompilationResult.CSharpOutputResult)
                new Compilation(
                    new CompilerOptions
                    {
                        OutputMode = OutputMode.CSharp,
                        AllowsImplicitModuleName = true,
                        DisablePrelude = true,
                        PrecompiledPackagePaths = { dllPath },
                    }
                ).Compile(fieldSource);
            Assert.True(
                csResult.Success,
                "C# compilation failed:\n" + string.Join("\n", csResult.Diagnostics.Diagnostics)
            );
            Assert.Contains("R_type", csResult.CsOutput);

            // IL backend: constructing the renamed precompiled record must resolve to the
            // baked type via the import alias and produce valid IL.
            var ctorSource =
                $@"(module test)
(import {moduleName})
(define (make-r) : R (R 10))";
            var ilResult = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.Il,
                    AllowsImplicitModuleName = true,
                    DisablePrelude = true,
                    PrecompiledPackagePaths = { dllPath },
                }
            ).Compile(ctorSource);
            Assert.True(
                ilResult.Success,
                "IL compilation failed:\n" + string.Join("\n", ilResult.Diagnostics.Diagnostics)
            );
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void PrecompiledRecordFieldAccess_Il_ResolvesFieldGetter()
    {
        var (dllPath, moduleName, cleanup) = BuildPrecompiledTypeCollisionPackage();
        try
        {
            // Field access on the non-renamed precompiled record `r`. The single-letter
            // record name must survive cross-module type export (GeneralizeForExport) so the
            // receiver is inferred as `r`, not an unconstrained type variable — otherwise the
            // IL backend cannot resolve the getter and emits a stack-imbalanced method.
            var source =
                $@"(module test)
(import {moduleName})
(define (compute) : Int (r/a (r 7)))";
            var result = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.Il,
                    AllowsImplicitModuleName = true,
                    DisablePrelude = true,
                    PrecompiledPackagePaths = { dllPath },
                }
            ).Compile(source);
            Assert.True(
                result.Success,
                "IL compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
            );
            var asm = Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes);
            var compute = asm.GetExportedTypes()
                .SelectMany(t => t.GetMethods())
                .First(m =>
                    m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                    && m.GetParameters().Length == 0
                );
            Assert.Equal(7, compute.Invoke(null, null));
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    ///     Compiles a tiny "colltype" package whose two exported record types sanitize to
    ///     the same identifier (`r`/`R`), forcing EmitNameResolver to rename one and persist
    ///     the type rename into metadata. Returns the DLL path and a cleanup callback.
    /// </summary>
    private static (
        string DllPath,
        string ModuleName,
        Action Cleanup
    ) BuildPrecompiledTypeCollisionPackage()
    {
        // Each build gets a unique module/assembly name and namespace. Several tests compile
        // this package and load it via Assembly.LoadFrom, which binds by assembly name — a
        // shared name would collide ("assembly already loaded") across tests in one process,
        // and a shared namespace would let ResolveClrTypeForTypeRef pick another test's copy.
        var token = Guid.NewGuid().ToString("N");
        var moduleName = "colltype" + token;
        var ns = "Colltype" + token + ".Pkg";

        var pkgSrc = Path.Combine(Path.GetTempPath(), $"zs_pkgsrc_{Guid.NewGuid():N}");
        var pkgOut = Path.Combine(Path.GetTempPath(), $"zs_pkgout_{Guid.NewGuid():N}");
        Directory.CreateDirectory(pkgSrc);
        Directory.CreateDirectory(pkgOut);

        File.WriteAllText(
            Path.Combine(pkgSrc, $"{moduleName}.zs"),
            $"(module {moduleName})\n"
                + "(export r R)\n"
                + "(define-record r [a : Int])\n"
                + "(define-record R [b : Int])"
        );

        var manifest = new PackageManifest(
            moduleName,
            "0.1.0",
            null,
            moduleName,
            moduleName,
            null,
            null,
            new PackageDependencies([], []),
            new PackageDependencies([], []),
            new BuildConfig(new MainBuildConfig(null, null, ns, []), null),
            null,
            SourceSpan.None
        );

        var diag = new DiagnosticBag();
        var libResult = new LibraryCompiler(diag).Compile(
            pkgSrc,
            manifest,
            new CompilerOptions { OutputMode = OutputMode.Il, DisablePrelude = true }
        );
        Assert.True(
            libResult is not null && !diag.HasErrors,
            "Package compilation failed:\n" + string.Join("\n", diag.Diagnostics)
        );

        // The type rename must have been recorded for the colliding exported type.
        var collModule = libResult!.Modules.Values.First(m => m.ExportedNames.Contains("R"));
        Assert.Equal("R_type", collModule.TypeEmittedNames!["R"]);

        var dllPath = Path.Combine(pkgOut, $"{moduleName}.dll");
        File.WriteAllBytes(dllPath, libResult.AssemblyBytes);
        File.WriteAllText(
            Path.ChangeExtension(dllPath, ".metadata.json"),
            MetadataSerializer.Serialize(
                moduleName,
                "0.1.0",
                moduleName,
                libResult.Modules,
                moduleName,
                moduleName
            )
        );

        return (
            dllPath,
            moduleName,
            () =>
            {
                try
                {
                    Directory.Delete(pkgSrc, true);
                }
                catch
                {
                    // best-effort cleanup
                }

                try
                {
                    Directory.Delete(pkgOut, true);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        );
    }

    [Fact]
    public void PolymorphicEquality_NullCheck_Il()
    {
        var source =
            @"(module test)
(define (is-null? [x : String]) : Bool
  (= x null))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Contains("null", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 1
            );
        Assert.Equal(true, method.Invoke(null, [null]));
        Assert.Equal(false, method.Invoke(null, ["hello"]));
    }

    [Fact]
    public void PolymorphicEquality_StringComparison_Il()
    {
        var source =
            @"(module test)
(define (same? [a : String] [b : String]) : Bool
  (= a b))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
    }

    [Fact]
    public void BoxingToSystemObject_CSharp()
    {
        var source =
            @"
(import stdlib/mutable/hash)

(define (put-float [m : (Mutable-Hash String System.Object)] [v : Float]) : Unit
  (hash-set! m ""key"" v))";
        var cs = Compile(source);
        Assert.Contains("PutFloat", cs);
    }

    [Fact]
    public void NullableWidening_FloatToNullableFloat_CSharp()
    {
        var source =
            @"
(define-class Timer
  [duration : Float? #:mutable]
  (constructor
    (set! duration 3.0)))";
        var cs = Compile(source);
        Assert.Contains("Duration", cs);
    }

    [Fact]
    public void NullableWidening_FloatToNullableFloat_Il()
    {
        var source =
            @"(module test)
(define-class Timer
  [duration : Float? #:mutable]
  (constructor
    (set! duration 3.0))
  (define (GetDuration) : Float? duration))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

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
        var source =
            @"(module test)
(define-class Timer
  [duration : Float? #:mutable]
  (constructor
    (set! duration null))
  (define (GetDuration) : Float? duration))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

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
        var source =
            @"(module test)
(define-class Counter
  [value : Int? #:mutable]
  (constructor
    (set! value null))
  (define (SetValue [v : Int]) : Unit
    (set! value v))
  (define (GetValue) : Int? value))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

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
        var source =
            @"(module test)
(import-clr
  [friday System.DayOfWeek/Friday
    : (-> System.DayOfWeek)])

(define (get-friday) : System.DayOfWeek
  (friday))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m => m.Name.Contains("Friday", StringComparison.OrdinalIgnoreCase));
        var value = method.Invoke(null, []);
        Assert.Equal(DayOfWeek.Friday, value);
    }

    [Fact]
    public void StaticField_StringEmpty_Il()
    {
        var source =
            @"(module test)
(import-clr
  [empty-string System.String/Empty
    : (-> String)])

(define (get-empty) : String
  (empty-string))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m => m.Name.Contains("Empty", StringComparison.OrdinalIgnoreCase));
        var value = method.Invoke(null, []);
        Assert.Equal("", value);
    }

    // ===== Boxing end-to-end tests =====

    [Fact]
    public void Boxing_FloatToObject_InDictionary_Il()
    {
        // Test that Float can be stored in a Dictionary<string, object> via hash-set!
        var source =
            @"(module test)
(import stdlib/mutable/hash)

(define (store-float) : (Mutable-Hash String System.Object)
  (let ([m (make-hash)])
    (begin
      (hash-set! m ""key"" 3.14)
      m)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
    }

    [Fact]
    public void Boxing_IntToObject_ViaClrCall_Il()
    {
        // Test that Int can be passed to a CLR method expecting System.Object
        var source =
            @"(module test)
(import-clr
  [writeln System.Console/WriteLine : (System.Object -> Unit)])

(define (log-int [v : Int]) : Unit
  (writeln v))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
    }

    // ===== Nullable wrapping end-to-end tests with runtime verification =====

    [Fact]
    public void NullableWidening_MultipleFields_Il()
    {
        var source =
            @"(module test)
(define-class Effect
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

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

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
        var source =
            @"(module test)
(define (eq? [a : Int] [b : Int]) : Bool
  (= a b))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m => m.GetParameters().Length == 2 && m.ReturnType == typeof(bool));
        Assert.Equal(true, method.Invoke(null, [5, 5]));
        Assert.Equal(false, method.Invoke(null, [5, 7]));
    }

    [Fact]
    public void NullableReceiver_PropertyAccess_Il()
    {
        // Regression test: property access on a nullable receiver type should resolve
        // the property on the unwrapped type, not emit ldc.i4.0 fallback
        var source =
            @"(module test)
(import-clr
  [uri-host System.Uri.Host
    :instance-property : (System.Uri -> String)]
  System)

(define (get-host [u : System.Uri?]) : String
  (if (= u null) ""none"" (uri-host u)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
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
        var source =
            @"
(define-class MyDisposable : System.IDisposable
  [disposed : Bool #:mutable]
  (constructor (set! disposed #f))
  (define (Dispose) : Unit
    (set! disposed #t)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var type = asm.GetExportedTypes().First(t => t.Name == "MyDisposable");

        Assert.True(
            typeof(IDisposable).IsAssignableFrom(type),
            $"Expected MyDisposable to implement IDisposable. Interfaces: [{string.Join(", ", type.GetInterfaces().Select(i => i.Name))}]"
        );
        Assert.Contains(typeof(IDisposable), type.GetInterfaces());
    }

    [Fact]
    public void ClassDecl_ZSchemeInterface_ImplementsInterface_Il()
    {
        var source =
            @"
(define-interface IGreeter
  (Greet [] : String))

(define-class HelloGreeter : IGreeter
  [name : String]
  (define (Greet) : String name))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var greeterInterface = asm.GetExportedTypes().First(t => t.Name == "IGreeter");
        var helloType = asm.GetExportedTypes().First(t => t.Name == "HelloGreeter");

        Assert.True(
            greeterInterface.IsAssignableFrom(helloType),
            "Expected HelloGreeter to implement IGreeter"
        );
    }

    [Fact]
    public void ClassDecl_InstanceMethodSlashCall_Il()
    {
        var source =
            @"
(define-class Counter
  [value : Int]
  (define (next) : Int (+ value 1)))
(define (get-next [c : Counter]) : Int (Counter/next c))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var counterType = asm.GetExportedTypes().First(t => t.Name == "Counter");
        var counter = Activator.CreateInstance(counterType, 41)!;
        var moduleType = asm.GetExportedTypes().First(t => t.GetMethod("GetNext") is not null);
        var getNext = moduleType.GetMethod("GetNext")!;
        var result2 = (int)getNext.Invoke(null, [counter])!;
        Assert.Equal(42, result2);
    }

    [Fact]
    public void With_Expression_EmitsCSharpWith()
    {
        var source =
            @"(module test)
(define-record Point [x : Int] [y : Int])
(define (shift [p : Point] [nx : Int]) : Point
  (with p [x nx]))";
        var cs = Compile(source);
        Assert.Contains(" with { X = nx }", cs);
    }

    [Fact]
    public void With_MultipleFields_EmitsCSharpWith()
    {
        var source =
            @"(module test)
(define-record Point [x : Int] [y : Int])
(define (move [p : Point] [nx : Int] [ny : Int]) : Point
  (with p [x nx] [y ny]))";
        var cs = Compile(source);
        Assert.Contains(" with { X = nx, Y = ny }", cs);
    }

    [Fact]
    public void With_Expression_Il_RoundtripExecutes()
    {
        var source =
            @"
(define-record Point [x : Int] [y : Int])
(define (shift-x [p : Point] [nx : Int]) : Point
  (with p [x nx]))
(define (move-to [p : Point] [nx : Int] [ny : Int]) : Point
  (with p [x nx] [y ny]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var pointType = asm.GetExportedTypes().First(t => t.Name == "Point");
        var moduleType = asm.GetExportedTypes().First(t => t.Name.EndsWith("Module"));

        // Has <Clone>$ method (required for decompilers to render `with`).
        Assert.NotNull(
            pointType.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance)
        );
        // Has copy constructor.
        Assert.NotNull(
            pointType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [pointType])
        );
        // Has PrintMembers method.
        Assert.NotNull(
            pointType.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.NonPublic)
        );
        // Has EqualityContract.
        Assert.NotNull(
            pointType.GetProperty(
                "EqualityContract",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
        );

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
        var source =
            @"(module test)
(define-struct Point [x : Int] [y : Int])";
        var cs = Compile(source);
        Assert.Contains("public readonly record struct Point(int X, int Y);", cs);
    }

    [Fact]
    public void Struct_NewForm_EmitsCtorCall()
    {
        // Verifies the (new ...) phase-ordering fix: user-defined struct names resolve
        // through the record-ctor path rather than CLR reflection.
        var source =
            @"(module test)
(define-struct Point [x : Int] [y : Int])
(define (mk) : Point (new Point 3 4))";
        var cs = Compile(source);
        Assert.Contains("new Point(X: 3, Y: 4)", cs);
    }

    [Fact]
    public void Struct_With_EmitsCSharpWithExpression()
    {
        var source =
            @"(module test)
(define-struct Point [x : Int] [y : Int])
(define (shift [p : Point] [nx : Int]) : Point (with p [x nx]))";
        var cs = Compile(source);
        Assert.Contains("with { X = nx }", cs);
    }

    [Fact]
    public void Struct_Il_RoundtripExecutes_ValueSemantics()
    {
        // The defining test for value semantics: shifting a Point produces a fresh value;
        // the source must remain unchanged because structs are stack-copied.
        var source =
            @"
(define-struct Point [x : Int] [y : Int])
(define (shift-x [p : Point] [nx : Int]) : Point (with p [x nx]))
(define (move-to [p : Point] [nx : Int] [ny : Int]) : Point
  (with p [x nx] [y ny]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var pointType = asm.GetExportedTypes().First(t => t.Name == "Point");
        var moduleType = asm.GetExportedTypes().First(t => t.Name.EndsWith("Module"));

        // Real CLR struct.
        Assert.True(pointType.IsValueType);
        Assert.Equal(typeof(ValueType), pointType.BaseType);
        // No <Clone>$ on structs.
        Assert.Null(
            pointType.GetMethod(
                "<Clone>$",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            )
        );

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
        var source =
            @"
(define-record Point [x : Int] [y : Int])
(define (mk) : Point (new Point 3 4))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var pointType = asm.GetExportedTypes().First(t => t.Name == "Point");
        var moduleType = asm.GetExportedTypes().First(t => t.Name.EndsWith("Module"));

        var made = moduleType.GetMethod("Mk")!.Invoke(null, []);
        Assert.NotNull(made);
        Assert.Equal(3, pointType.GetProperty("X")!.GetValue(made));
        Assert.Equal(4, pointType.GetProperty("Y")!.GetValue(made));
    }

    // ─── Match against record/struct constructor patterns (IL backend) ──────────
    //
    // Fuzzer seed 0x13c176f2 (and many siblings) generated `(match v [(SRec_0 a b) ...])`
    // expressions where SRec_0 was a user-defined struct or record. The IL emitter only
    // populated its case-pattern dictionaries for union cases, so the match raised
    // "Cannot resolve constructor type 'SRec_0' for pattern match" even though the C#
    // backend handled the same input. The fix registers records and structs under a
    // self-referential case key (`{name}.{name}`) and skips the isinst check (and uses
    // ldloca/call instead of ldloc/callvirt) when the constructor name already equals
    // the static scrutinee type — required so value-type structs verify.

    [Fact]
    public void Match_RecordConstructorPattern_Il_RoundtripExecutes()
    {
        var source =
            @"
(define-record Point [x : Int] [y : Int])
(define (test) : Int
  (match (Point 10 20)
    [(Point a b) (+ a b)]))";
        var bytes = CompileToIlBytesNoPrelude(source);
        Assert.Equal(30, InvokeZeroArgIntMethod(bytes, "Test"));
    }

    [Fact]
    public void Match_StructConstructorPattern_Il_RoundtripExecutes()
    {
        var source =
            @"
(define-struct Point [x : Int] [y : Int])
(define (test) : Int
  (match (Point 7 9)
    [(Point a b) (+ a b)]))";
        var bytes = CompileToIlBytesNoPrelude(source);
        Assert.Equal(16, InvokeZeroArgIntMethod(bytes, "Test"));
    }

    [Fact]
    public void Match_StructInsideTuplePattern_Il_RoundtripExecutes()
    {
        // Mirrors the fuzzer-generated nested form: (match (values (P ...) z) [(values (P a b) c) ...]).
        // Exercises both the new same-type record/struct dispatch and the recursive sub-pattern emission.
        var source =
            @"
(define-struct Point [x : Int] [y : Int])
(define (test) : Int
  (match (values (Point 1 2) 3)
    [(values (Point a b) c) (+ a (+ b c))]
    [_ 0]))";
        var bytes = CompileToIlBytesNoPrelude(source);
        Assert.Equal(6, InvokeZeroArgIntMethod(bytes, "Test"));
    }

    private static byte[] CompileToIlBytesNoPrelude(string source)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return ((CompilationResult.IlOutputResult)result).OutputBytes;
    }

    private static int InvokeZeroArgIntMethod(byte[] bytes, string methodName)
    {
        var asm = Assembly.Load(bytes);
        var moduleType = asm.GetExportedTypes().First(t => t.Name.EndsWith("Module"));
        var method = moduleType.GetMethod(methodName)!;
        return (int)method.Invoke(null, [])!;
    }

    // ─── Async without await: non-generic Task and Task<T> ──────────

    [Fact]
    public void AsyncFunctionWithoutAwait_NonGenericTask()
    {
        var source =
            @"(module test)
(define-async (do-nothing) : Task 0)";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task DoNothing()", cs);
    }

    [Fact]
    public void AsyncFunctionWithoutAwait_TaskOfString()
    {
        var source =
            @"(module test)
(define-async (greet) : (Task String) ""hello"")";
        var cs = Compile(source);
        Assert.Contains("async System.Threading.Tasks.Task<string> Greet()", cs);
    }

    [Fact]
    public void AsyncClassMethodWithoutAwait_TaskOfInt()
    {
        var source =
            @"(module test)
(define-class Worker
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
        var source =
            @"(module test)
(define-class MathHelper
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
        var source =
            @"(module test)
(define (helper [x : Int]) : Int (+ x 10))
(define-class Worker
  (define (Compute [x : Int]) : Int (helper x)))";
        var cs = Compile(source);
        Assert.Contains("int Helper(int x)", cs);
        Assert.Contains("sealed class Worker", cs);
        Assert.Contains("int Compute(int x)", cs);
    }

    [Fact]
    public void ClassDecl_RecursiveMethodCall()
    {
        var source =
            @"(module test)
(define-class Counter
  (define (Countdown [n : Int]) : Int
    (if (= n 0) 0 (Countdown (- n 1)))))";
        var cs = Compile(source);
        Assert.Contains("sealed class Counter", cs);
        Assert.Contains("int Countdown(int n)", cs);
    }

    [Fact]
    public void ImportedModule_LambdaWithCaptures_NestsClosureInOwnModule_Il()
    {
        // Regression: when a function inside an imported module body contains a
        // lambda with captured locals, the IL emitter previously created the
        // closure type nested inside the *main* module class (because Pass 0b
        // for imported module bodies didn't update _currentTypeDefinition).
        // The aux module's call site then referenced a NestedPrivate closure
        // type from a different declaring type — failing IL verification with
        // "Method/Field is not visible" and tripping InvalidProgramException at
        // runtime. The fix sets _currentTypeDefinition to each imported
        // module's TypeDefinition before emitting its function bodies, so the
        // lifted closure ends up nested under the right class.
        //
        // The lambda is returned (a first-class value) rather than immediately
        // invoked so that IIFE beta-reduction leaves it as a real closure.
        var dir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "aux_helper.zs"),
                @"
(module aux_helper)
(define (aux_helper/make-adder [x : Int]) : (Int -> Int)
  (lambda ([y : Int]) (+ x y)))
(export aux_helper/make-adder)"
            );

            var mainSource =
                @"
(module main_test)
(import aux_helper)
(define (compute) : Int
  ((aux_helper/make-adder 5) 10))";
            var mainPath = Path.Combine(dir, "main_test.zs");
            File.WriteAllText(mainPath, mainSource);

            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.Il,
                    AllowsImplicitModuleName = true,
                    PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
                }
            );
            var result = compilation.Compile(mainSource, mainPath);
            Assert.True(
                result.Success,
                "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
            );

            var ilResult = (CompilationResult.IlOutputResult)result;
            var asm = Assembly.Load(ilResult.OutputBytes);

            // The imported aux module's class should own all closure types lifted
            // from its function bodies. If any closure ends up nested in the main
            // module class instead, the bug has regressed.
            var auxModule = asm.GetExportedTypes()
                .First(t => t.Name.Equals("Aux_HelperModule", StringComparison.OrdinalIgnoreCase));
            var auxClosures = auxModule
                .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                .Where(t => t.Name.StartsWith("<>c__", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(auxClosures);

            var mainModule = asm.GetExportedTypes()
                .First(t => t.Name.Equals("Main_TestModule", StringComparison.OrdinalIgnoreCase));
            var mainClosures = mainModule
                .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                .Where(t => t.Name.StartsWith("<>c__", StringComparison.Ordinal))
                .ToList();
            Assert.Empty(mainClosures);

            // End-to-end: the lifted lambda must execute without
            // InvalidProgramException. (5 + 10) = 15.
            var compute = mainModule.GetMethod(
                "Compute",
                BindingFlags.Public | BindingFlags.Static,
                []
            )!;
            Assert.Equal(15, compute.Invoke(null, null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ImportedModule_LambdaWithoutCaptures_NestsLambdaInOwnModule_Il()
    {
        // Companion regression to the closure-nesting fix: even a capture-free
        // lambda inside an imported module's function body was being emitted
        // into the main module's class via _currentTypeDefinition, producing
        // calls across declaring types. The lambda body itself is public-static
        // so the invalid-program failure is subtler than the closure case, but
        // it still leaves the assembly with the wrong type layout. Lock the
        // shape in. The lambda is returned (first-class) rather than immediately
        // invoked so that IIFE beta-reduction leaves it as a real lambda method.
        var dir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "aux_pure.zs"),
                @"
(module aux_pure)
(define (aux_pure/make-inc) : (Int -> Int)
  (lambda ([y : Int]) (+ y 1)))
(export aux_pure/make-inc)"
            );

            var mainSource =
                @"
(module main_test2)
(import aux_pure)
(define (compute) : Int
  ((aux_pure/make-inc) 41))";
            var mainPath = Path.Combine(dir, "main_test2.zs");
            File.WriteAllText(mainPath, mainSource);

            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.Il,
                    AllowsImplicitModuleName = true,
                    PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
                }
            );
            var result = compilation.Compile(mainSource, mainPath);
            Assert.True(
                result.Success,
                "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
            );

            var ilResult = (CompilationResult.IlOutputResult)result;
            var asm = Assembly.Load(ilResult.OutputBytes);

            var auxModule = asm.GetExportedTypes()
                .First(t => t.Name.Equals("Aux_PureModule", StringComparison.OrdinalIgnoreCase));
            var auxLambdas = auxModule
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("__lambda_", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(auxLambdas);

            var mainModule = asm.GetExportedTypes()
                .First(t => t.Name.Equals("Main_Test2Module", StringComparison.OrdinalIgnoreCase));
            var mainLambdas = mainModule
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("__lambda_", StringComparison.Ordinal))
                .ToList();
            Assert.Empty(mainLambdas);

            var compute = mainModule.GetMethod(
                "Compute",
                BindingFlags.Public | BindingFlags.Static,
                []
            )!;
            Assert.Equal(42, compute.Invoke(null, null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ObjectExpr_MethodBodyCallsModuleFunction_ExecutesCorrectly_Il()
    {
        // Regression: the C# emitter captured module-level function references
        // into anonymous-class fields typed as `object`, producing `new __Object_0(helper, v)`
        // (helper undefined at that scope) and `this.Helper_field(this.V_field)`
        // (cannot invoke `object`). The IL emitter silently dropped the module
        // ref from its capture list but could still mis-type the remaining
        // captures in related paths, so the fix also retyped capture fields to
        // their ZType. Execute end-to-end to lock in both halves.
        var source =
            @"(module test)
(define-interface IBox
  (get : Int))

(define (helper [x : Int]) : Int (+ x 10))

(define (make-box [v : Int]) : IBox
  (object IBox
    (define (get) : Int (helper v))))

(define (compute [v : Int]) : Int
  (IBox/get (make-box v)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 1
            );
        Assert.Equal(15, compute.Invoke(null, [5]));
    }

    // Regression: `with-handlers` with two or more catch clauses used to
    // generate an invalid exception table (an orphan `nop` was left between
    // consecutive handler regions), and the CLR raised
    // InvalidProgramException when the method was JIT-compiled. See the
    // companion IlEmitter regression test for the low-level metadata check.
    // Originally surfaced by the fuzzer on seed 0x00000539, case 0x40407949.
    [Fact]
    public void WithHandlers_MultipleCatch_NoBodyThrow_Il()
    {
        var source =
            @"(module test)
(define (compute) : Int
  (with-handlers ([System.ArgumentException x] 17)
                  ([System.Exception y] 18)
     99))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(99, compute.Invoke(null, null));
    }

    [Fact]
    public void WithHandlers_MultipleCatch_FirstHandlerMatches_Il()
    {
        // The body throws ArgumentException, which matches the first catch
        // clause. This exercises both handler regions: the body must reach
        // the first handler (17) without falling through into the second.
        var source =
            @"(module test)
(define (compute) : Int
  (with-handlers ([System.ArgumentException x] 17)
                  ([System.Exception y] 18)
     (raise (new System.ArgumentException ""boom""))))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(17, compute.Invoke(null, null));
    }

    [Fact]
    public void WithHandlers_MultipleCatch_SecondHandlerMatches_Il()
    {
        // Body throws InvalidOperationException, which falls through the
        // first (ArgumentException) clause and is caught by the second
        // (Exception) clause. Confirms control transfers across the
        // previously-buggy inter-handler boundary.
        var source =
            @"(module test)
(define (compute) : Int
  (with-handlers ([System.ArgumentException x] 17)
                  ([System.Exception y] 18)
     (raise (new System.InvalidOperationException ""boom""))))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(18, compute.Invoke(null, null));
    }

    // Regression: a `with-handlers` (try/catch) used as a super-constructor
    // argument used to crash the IL backend with a StackImbalanceException
    // when AsmResolver built the PE image. The constructor emitted
    // `Ldarg_0; <super-args>; Call <base-ctor>`, so when a super arg expanded
    // to a `try` block the verifier saw the `this` reference still on the
    // stack at the protected region's entry — IL requires the evaluation
    // stack to be empty at try-block entry. Originally surfaced by the
    // fuzzer in stack imbalance failures across object expressions and class
    // declarations alike (seeds 0x4356b08c, 0x7f04de93, 0x5ac1985a, etc.).
    [Fact]
    public void ObjectExpr_WithHandlersInSuperArg_Il()
    {
        var source =
            @"(namespace TestNs)
(module test)

(define-class #:open Animal
  [age : Int #:mutable]
  (define (Speak) : Int age))

(define (compute) : Int
  (let ([a (object : Animal
    (constructor (super
      (with-handlers ([System.Exception x] 1) 7)))
    (define (Speak) : Int 5))])
    (Animal/Speak a)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // The Animal/Speak override on the anonymous object returns 5
        // regardless of `age`. We just need the ctor to run without
        // ExecutionEngineException / VerificationException.
        Assert.Equal(5, compute.Invoke(null, null));
    }

    // Regression (companion to ObjectExpr_WithHandlersInSuperArg_Il): the
    // same Ldarg_0 + super-args + Call shape lives in regular `class`
    // declarations, so a try/catch in a derived class's super call hit the
    // identical stack-imbalance path.
    [Fact]
    public void ClassDecl_WithHandlersInSuperArg_Il()
    {
        var source =
            @"(namespace TestNs)
(module test)

(define-class #:open Animal
  [age : Int #:mutable]
  (define (Speak) : Int age))

(define-class Dog : Animal
  (constructor (super
    (with-handlers ([System.Exception x] 1) 13)))
  (define (Speak) : Int 5))

(define (compute) : Int
  (Dog/Speak (new Dog)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(5, compute.Invoke(null, null));
    }

    // Regression: the same Ldarg_0 + value + Stfld pattern is used for
    // `(set! field expr)` forms in a class constructor. A `with-handlers` in
    // the value expression also tripped the empty-stack-at-try-entry check.
    [Fact]
    public void ClassDecl_WithHandlersInFieldSet_Il()
    {
        var source =
            @"(namespace TestNs)
(module test)

(define-class Box
  [value : Int #:mutable]
  (constructor
    (set! value (with-handlers ([System.Exception x] 1) 42))))

(define (compute) : Int
  (Box/value (new Box)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(42, compute.Invoke(null, null));
    }

    // Regression: variant of ObjectExpr_WithHandlersInSuperArg_Il where the
    // try/catch actually catches an exception thrown from the body, ensuring
    // the spilled-local path doesn't accidentally short-circuit the handler.
    [Fact]
    public void ObjectExpr_WithHandlersInSuperArg_HandlerCatches_Il()
    {
        var source =
            @"(namespace TestNs)
(module test)

(define-class #:open Animal
  [age : Int #:mutable]
  (define (Age) : Int age))

(define (compute) : Int
  (let ([a (object : Animal
    (constructor (super
      (with-handlers ([System.Exception x] 7)
        (raise (new System.Exception ""boom"")))))
    (define (Age) : Int 0))])
    (Animal/age a)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // Body raises, handler returns 7 — that becomes the value passed to
        // (super ...), which sets the inherited `age` field.
        Assert.Equal(7, compute.Invoke(null, null));
    }

    [Fact]
    public void LambdaInsideClassMethod_ReadsClassField_Il()
    {
        // Regression (fuzzer seed 0x489fcc19): a lambda defined inside a class
        // instance method that reads a class field used to be emitted as a plain
        // static method on the class. At `ldfld f0`, `ldarg.0` pushed the lambda's
        // first parameter (int32) instead of `this` (FCls_0), so ilverify rejected
        // the IL with "found Int32, expected ref 'FCls_0'" and the JIT refused to
        // run it. The fix captures `this` in a synthetic `<>this` closure field.
        var source =
            @"(module test)
(define-class Counter
  [value : Int]
  (define (get-via-lambda [_ignored : Int]) : Int
    ((lambda ([dummy : Int]) value) 0)))

(define (compute) : Int
  (Counter/get-via-lambda (new Counter 42) 0))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // If the IL still emits the lambda as a static method, the JIT throws
        // InvalidProgramException on first invocation rather than returning 42.
        Assert.Equal(42, compute.Invoke(null, null));
    }

    [Fact]
    public void NestedLambdasInsideClassMethod_ReadClassField_Il()
    {
        // The enclosing lambda doesn't touch the class field directly, but the
        // inner one does. `BodyReferencesClassFields` / the free-var analysis
        // must surface that so the outer lambda captures `this`, which the inner
        // lambda then chains onto. Without this, the inner lambda's closure would
        // have no way to reach the class instance.
        var source =
            @"(module test)
(define-class Counter
  [value : Int]
  (define (nested [_p : Int]) : Int
    (((lambda ([x : Int]) (lambda ([y : Int]) (+ value (+ x y)))) 3) 4)))

(define (compute) : Int
  (Counter/nested (new Counter 10) 0))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(17, compute.Invoke(null, null));
    }

    [Fact]
    public void LambdaInsideClassMethod_ShadowsFieldWithLet_Il()
    {
        // A let-bound local with the same name as a class field must bind over
        // the field. The emitter's free-var capture loop already handles this,
        // but the `<>this` capture heuristic must also skip shadowed names —
        // otherwise we'd capture an unused `this` and the binding wouldn't match
        // the field's backing store anyway.
        var source =
            @"(module test)
(define-class Counter
  [value : Int]
  (define (shadowed [_p : Int]) : Int
    (let ([value 999])
      ((lambda ([x : Int]) value) 0))))

(define (compute) : Int
  (Counter/shadowed (new Counter 1) 0))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(999, compute.Invoke(null, null));
    }

    [Fact]
    public void LambdaInsideClassMethod_WritesMutableFieldViaSetBang_Il()
    {
        // `(set! field v)` doesn't bind the field name as a Var, so it's invisible
        // to FindFreeVars — only a dedicated SetField scan catches it. Without that
        // scan the lambda stays static and `stfld` writes through int32-on-stack
        // as if it were an FCls_0 reference.
        var source =
            @"(module test)
(define-class Counter
  [value : Int #:mutable]
  (define (write-via-lambda [x : Int]) : Int
    (begin
      ((lambda ([v : Int]) (set! value v)) x)
      value)))

(define (compute) : Int
  (Counter/write-via-lambda (new Counter 0) 7))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(7, compute.Invoke(null, null));
    }

    // ─── Float literal match patterns ────────────────────────────────
    // Regression: fuzzer seed 0xf0ab7e8f (and many siblings in the same run)
    // exposed two bugs in float-literal match patterns:
    //
    //   1. IlEmitter.EmitPatternTest had no case for `Literal { Value: float }`.
    //      The switch fell through to a no-op, meaning the test never emitted a
    //      `brfalse` to the next arm — so the *first* float-literal arm's body
    //      always ran, regardless of the scrutinee's value.
    //
    //   2. CSharpEmitter translated arms verbatim to switch-expression patterns
    //      like `-0f => ..., 0f => ...`. Roslyn rejects that pair with CS8510
    //      because IEEE 754 makes `-0.0 == 0.0`, so the second arm is statically
    //      unreachable.

    [Fact]
    public void Match_FloatLiteralPattern_Il_FallsThroughToWildcard()
    {
        // Without the IL fix, matching `5.0` against `[1.0 ...] [2.0 ...]`
        // always returned the first arm's body (10) because the pattern test
        // was a no-op. With the fix, the value falls through to the wildcard.
        var source =
            @"(module test)
(define (compute) : Int
  (match 5.0
    [1.0 10]
    [2.0 20]
    [_ 99]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(99, compute.Invoke(null, null));
    }

    [Fact]
    public void Match_FloatLiteralPattern_Il_MatchesExactLiteral()
    {
        // Companion: when a float literal arm does match, we pick the matching
        // arm's body rather than the first one.
        var source =
            @"(module test)
(define (compute) : Int
  (match 2.0
    [1.0 10]
    [2.0 20]
    [_ 99]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(20, compute.Invoke(null, null));
    }

    [Fact]
    public void Match_FloatLiteralPattern_Il_NegativeZeroMatchesPositiveZero()
    {
        // IEEE 754 treats `-0.0 == 0.0` as true, so matching `0.0` against
        // `[-0.0 ...] [0.0 ...]` fires the *first* arm. This mirrors C#'s
        // switch-expression semantics on float literals.
        var source =
            @"(module test)
(define (compute) : Int
  (match 0.0
    [-0.0 111]
    [0.0 222]
    [_ 999]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(111, compute.Invoke(null, null));
    }

    [Fact]
    public void Match_FloatLiteralPattern_NestedInTuple_Il_FallsThrough()
    {
        // EmitTuplePatternTest recurses via EmitPatternTest, so a missing float
        // case would also cause every tuple with a float sub-pattern to match
        // incorrectly. Guard that path explicitly — match (5.0, 7) against
        // tuple arms that demand 1.0 or 2.0 should skip to the wildcard.
        var source =
            @"(module test)
(define (compute) : Int
  (match (values 5.0 7)
    [(values 1.0 x) 100]
    [(values 2.0 x) 200]
    [_ 999]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(999, compute.Invoke(null, null));
    }

    [Fact]
    public void Match_FloatLiteralPattern_Cs_DropsIeee754EquivalentArms()
    {
        // Without the C# fix, the fuzzer's `-0.0` and `0.0` arms both reached
        // the emitter and produced `-0f => ..., 0f => ...`, which Roslyn
        // rejects with CS8510. PruneUnreachableArms now drops any float
        // literal that's IEEE 754-equal to an earlier one — the emitted
        // source should round-trip through Roslyn cleanly.
        var source =
            @"(module test)
(define (compute) : Int
  (match 0.0
    [-0.0 111]
    [0.0 222]
    [_ 999]))";

        var cs = Compile(source);
        // Second-matching float arm is pruned; only `-0f` remains.
        Assert.Contains("-0f => 111", cs);
        Assert.DoesNotContain("0f => 222", cs);
    }

    [Fact]
    public void With_InnerLet_UnionCtor_ResolvesTypeParamsFromAnnotation()
    {
        // Fuzzer regression (seed 0xf2b485a9): a `let` inside a `with` update
        // value had `: (Result Int String)` as its annotation and `(Ok n)` as
        // its RHS. The nested `Ok`'s error-type parameter was never applied with
        // the substitution unified against the annotation, so the C# emitter
        // printed `new Ok<int, object>(...)` inside a lambda whose parameter
        // expected `Result<int, string>` — Roslyn then rejected the program
        // with CS1503 (cannot convert `Ok<int, object>` to `Result<int, string>`).
        //
        // The fix adds `AstNode.With` to `TypeInferer.Resolve` so that nested
        // sub-expressions under a `with`'s update values get the same final
        // substitution walk as the rest of the AST.
        var source =
            @"(module test)
(import stdlib/result)
(define-record (FRec ^a) [val : ^a])
(define (compute) : Int
  (FRec/val (with (FRec 0) [val (let ([x : (Result Int String) (Ok 42)])
                                   (match x [(Ok v) v] [(Err _) 0]))])))";

        var cs = Compile(source);
        // The Ok constructor must carry the annotation's `string` error type,
        // not the default-to-`object` fallback produced by an unresolved type var.
        Assert.Contains("new Stdlib_ResultModule.Ok<int, string>(42)", cs);
        Assert.DoesNotContain("Ok<int, object>", cs);
    }

    [Fact]
    public void With_InnerLet_UnionCtor_IlRoundtripExecutes()
    {
        // Runtime companion to With_InnerLet_UnionCtor_ResolvesTypeParamsFromAnnotation:
        // the fuzzer flagged this via the compile-consistency oracle (Roslyn
        // couldn't emit the C# at all), so the IL backend produced output
        // while the C# backend failed. Execute the IL output to confirm the
        // program's observable behavior is preserved after the fix.
        var source =
            @"
(import stdlib/result)
(define-record (FRec ^a) [val : ^a])
(define (compute) : Int
  (FRec/val (with (FRec 0) [val (let ([x : (Result Int String) (Ok 42)])
                                   (match x [(Ok v) v] [(Err _) 0]))])))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(42, compute.Invoke(null, null));
    }

    [Fact]
    public void SetField_UnionCtor_ResolvesTypeParamsFromFieldType()
    {
        // Sibling of With_InnerLet_UnionCtor_ResolvesTypeParamsFromAnnotation:
        // `AstNode.SetField`'s `Value` subtree was also skipped by
        // `TypeInferer.Resolve`, so `(set! field (Ok 99))` on a field declared
        // as `(Result Int String)` produced `new Ok<int, object>(...)` paired
        // against patterns emitted with the correct `Ok<int, string>` — the
        // match then hits the fallback throw at runtime because the wrong
        // generic case type is never matched.
        var source =
            @"(module test)
(import stdlib/result)
(define-class FCls [v : (Result Int String) #:mutable]
  (define (stash) : Int
    (begin (set! v (Ok 99))
           (match v [(Ok n) n] [(Err _) 0]))))";

        var cs = Compile(source);
        Assert.Contains("new Stdlib_ResultModule.Ok<int, string>(99)", cs);
        Assert.DoesNotContain("Ok<int, object>", cs);
    }

    [Fact]
    public void Match_NestedConstructorPattern_OverPrecompiledUnion_BindsInnerVar_Il()
    {
        // Regression (fuzzer seed 0x14b60c9d, repro reduced to `(Some (Some y)) => y`):
        // for an imported union like stdlib's Option, EmitConstructorPatternTest
        // recursed into nested patterns only when ComputeUnionFieldZType could
        // resolve the field's ZType. That dictionary was populated for unions
        // emitted in the current module but never for precompiled unions, so
        // nested constructor patterns over imported types silently dropped
        // their inner bindings — the body `y` then failed with
        // `Variable 'y' not found for AsmResolver IL emission`.
        var source =
            @"(module test)
(import stdlib/option)
(define (compute) : Int
  (match (Some (Some 7))
    [(Some (Some y)) y]
    [(Some None) 0]
    [None 0]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(7, compute.Invoke(null, null));
    }

    [Fact]
    public void Match_NestedConstructorPattern_OverPrecompiledResult_BindsInnerVar_Il()
    {
        // Companion to the Option-nested-pattern test, exercising a precompiled
        // union with two type parameters. `Result<a, b>.Ok.Value : a` requires
        // ComputeUnionFieldZType to substitute the *first* type arg, not just
        // the nullary case — the registration must record both arity and
        // parameter ordering so subsequent recursion against
        // `(Result Int String)` resolves the inner scrutinee to `Option<Int>`.
        var source =
            @"(module test)
(import stdlib/option)
(import stdlib/result)
(define (compute) : Int
  (match (Ok (Some 11))
    [(Ok (Some n)) n]
    [(Ok None) -1]
    [(Err _) -2]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(11, compute.Invoke(null, null));
    }

    [Fact]
    public void ObjectExpr_InsideOuterObjectMethodBody_CapturesEnclosingClassField_Il()
    {
        // Regression (fuzzer seed 0x14b60c9d, second bug in the same case):
        // An object expression nested in another object's method body that
        // reads an enclosing-scope variable available only as a class field
        // of the outer anonymous class was not being captured. EmitObjectExpr
        // only collected captures from `locals` and the enclosing method's
        // `outerParams` — it never consulted `_currentClassFields`. The inner
        // method body's lookup then fell through every path and emitted
        // `Variable 'X' not found for AsmResolver IL emission`. With the fix,
        // the free var is captured by the inner anonymous class so its method
        // body resolves the read against `this.<field>`.
        var source =
            @"(module test)
(define-class #:open Cls
  [f0 : Int #:mutable]
  (define (Get) : Int f0))

(define (compute) : Int
  (let ([x 13])
    (let ([outer (object : Cls
      (constructor (super x))
      (define (Get) : Int
        (let ([inner (object : Cls
          (constructor (super 0))
          (define (Get) : Int x))])
          (Cls/Get inner))))])
      (Cls/Get outer))))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // outer.Get() returns inner.Get() which captures x = 13.
        Assert.Equal(13, compute.Invoke(null, null));
    }

    [Fact]
    public void ObjectExpr_CtorSuperArgs_UsingMatchBoundVar_TypedCorrectly_Il()
    {
        // Regression (fuzzer seed 0x2a1ae910): a free var bound by a match
        // pattern (e.g. `(Some x51)`) and referenced inside an object
        // expression's `(super ...)` arg was emitted with an `object`-typed
        // local instead of the var's actual type. The IL emitter's local
        // type came from `let.Value.Type`, which was an unresolved type
        // variable because TypeInferer.Resolve didn't descend into an
        // ObjectExpr's constructor — only its method bodies. Without
        // substitution, ZTypeVar mapped to System.Object and the local's
        // CIL type was `object`, while the value pushed via Ldarg was
        // `int32`, producing `[StackUnexpected] found Int32, expected ref
        // 'object'` under ilverify.
        var source =
            @"(namespace Repro)
(module test)
(define-class #:open FCls_0
  [f0 : Int #:mutable]
  [f1 : Int #:mutable]
  (define (M0_0 [p0 : Int]) : Int p0))

(define (compute) : Int
  (match (Some 7)
    [(Some x51)
      (let ([x54 (object : FCls_0
        (constructor (super (let ([x55 x51]) x55) 41))
        (define (M0_0 [p0 : Int]) : Int p0))])
        (FCls_0/f0 x54))]
    [None 0]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(7, compute.Invoke(null, null));
    }

    [Fact]
    public void ClassField_AccessFromSubclassMethod_VisibleUnderIlVerify()
    {
        // Regression (fuzzer seed 0x91bdf8b6, also 0xb162413e and others):
        // the IL backend emitted class field backing storage with
        // `FieldAttributes.Private`, then resolved a name reference to an
        // inherited field as a direct `ldfld` against the base class's
        // backing field. That access is illegal from a subclass — ilverify
        // reports `[FieldAccess] Field is not visible.` and the JIT throws
        // `FieldAccessException` at first call.
        //
        // The fix promotes class backing fields to `FieldAttributes.Family`
        // (protected), matching the semantics of public auto-properties:
        // subclass code — including the synthetic `(define-class Sub : Base ...)`
        // and `(object : Base ...)` lowerings — can now read and write the
        // inherited slot via ldfld/stfld without going through the public
        // getter.
        var source =
            @"(module test)
(define-class #:open Base
  [x : Int]
  (define (Get) : Int x))

(define-class Sub : Base
  (define (Get) : Int (+ x 1)))

(define (compute) : Int
  (Sub/Get (new Sub 41)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // Sub.Get reads inherited `x` (= 41) and adds 1.
        Assert.Equal(42, compute.Invoke(null, null));

        // Field-access flag check: every `Base` backing field must be
        // protected, not private. This is what makes the ldfld in `Sub::Get`
        // verifiable IL.
        var baseType = asm.GetExportedTypes().First(t => t.Name == "Base");
        var backing = baseType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.Name.Contains("BackingField"));
        Assert.NotNull(backing);
        Assert.True(
            backing!.IsFamily,
            $"{backing.Name} visibility was {backing.Attributes & FieldAttributes.FieldAccessMask}; expected Family"
        );
    }

    [Fact]
    public void NumericVar_FromUnusedUnionParam_DefaultsToInt_RunsCorrectlyIl()
    {
        // Regression (fuzzer seed 0x5096c465): arithmetic on a value extracted
        // from a polymorphic union case whose type parameter is otherwise
        // unconstrained used to leave a free `ZConstrainedVar` (numeric) in
        // the AST. Both type mappers (AsmResolver / IL and CSharp) fall
        // through to System.Object for unresolved type vars, so the IL
        // emitter produced `sub` on two object refs — ilverify rejected it
        // with "Expected numeric type on the stack" / "Unexpected type on the
        // stack [found ref 'object'][expected Int32]".
        //
        // The fix defaults free numeric ZConstrainedVars to their preferred
        // concrete kind (Int when allowed, otherwise the first allowed kind)
        // during the post-inference resolve pass, so codegen always sees a
        // real primitive type. Without the fix, this program loads but its
        // Compute() throws InvalidProgramException at JIT time because the
        // emitted IL is not verifiable.
        var source =
            @"(module test)
(define-union (FUn ^a ^b) (Left [lv : ^a]) (Right [rv : ^b]))

(define (compute) : Int
  (match (Left 99)
    [(Left _) 7]
    [(Right x) (let ([_ (- x x)]) 42)]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // Left 99 selects the first arm; Right is dead at runtime but its
        // body's IL still has to verify, which is the actual regression.
        // Before the fix this throws InvalidProgramException at JIT time
        // because the body of the Right arm contains `sub` on two object
        // refs (the union's second type arg fell through to System.Object).
        Assert.Equal(7, compute.Invoke(null, null));
    }

    [Fact]
    public void NumericVar_FromUnusedUnionParam_DefaultsToInt_CsharpCompiles()
    {
        // Same regression as above, but exercising the C# backend: before the
        // fix, the C# emitter produced `rv - rv` where `rv` was typed as
        // `object` (because the union's second type arg fell through to
        // System.Object). Roslyn rejects `object - object` with CS0019, so
        // the diff-exec oracle would never even reach the IL/C# comparison.
        // After the fix the union's second arg is `int`, and the body
        // compiles cleanly. We assert both that compilation succeeds and
        // that the emitted C# names the union as `<int, int>`.
        var source =
            @"(module test)
(define-union (FUn ^a ^b) (Left [lv : ^a]) (Right [rv : ^b]))

(define (compute) : Int
  (match (Left 99)
    [(Left _) 7]
    [(Right x) (let ([_ (- x x)]) 42)]))";

        var cs = Compile(source);
        Assert.Contains("Left<int, int>", cs);
        Assert.DoesNotContain("Left<int, object>", cs);
    }

    [Fact]
    public void MatchImportedUnionCtorPattern_BindsConcretePayloadType_RunsCorrectlyIl()
    {
        // Regression (fuzzer seed 0x13f68068 and many others, all reported as
        // `[StackUnexpected][found Int32][expected ref 'object']`): matching on a
        // union constructor that was imported from another module (e.g. stdlib's
        // `Some`) bound its payload variable to an unconstrained type variable.
        //
        // The cause was in pattern inference: `Pattern.Constructor` resolved the
        // constructor with `env.Lookup`, but imported constructors are registered
        // as *overloaded* names (resolved via `LookupOverloads`), so the lookup
        // returned null. The code then fell through to the "unknown constructor"
        // branch and bound the field to a fresh `FreshVar()` with no link to the
        // scrutinee, leaving it unresolved. Both type mappers fall through to
        // System.Object for unresolved vars, so when the bound value (an int32
        // local from `Some<int>.Value`) flowed into a ValueTuple, the IL emitter
        // pushed an int32 into an `object` field without boxing — invalid IL.
        //
        // The fix makes pattern inference fall back to the overload set, so the
        // payload variable `x` is correctly typed `Int`, the tuple becomes
        // `ValueTuple<int, int>`, and the IL verifies.
        //
        // Two details are load-bearing for the repro:
        //  * `Some` is an *imported* (overloaded) name, so the constructor lookup
        //    in pattern inference must go through the overload set. The prelude
        //    registers `Some`/`None` as plain bindings (which the old code
        //    resolved fine), so this mirrors the fuzzer's setup: an explicit
        //    `(import stdlib/option)` with the prelude disabled.
        //  * The inner arm returns the tuple element that did NOT come from `x`
        //    (`b`). Combining `x` with the other element (e.g. `(+ a b)`) would
        //    constrain `x` to Int through the arithmetic and mask the bug.
        var source =
            @"(module test)
(import stdlib/option)
(define (compute) : Int
  (match (Some 5)
    [(Some x) (match (values x 7) [(values a b) b])]
    [None 0]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // (Some 5) binds x = 5; the inner arm returns b = 7. Before the fix this
        // throws InvalidProgramException at JIT time because the tuple is
        // constructed as ValueTuple<object, int> with an unboxed int32 (x) pushed
        // into the object slot.
        Assert.Equal(7, compute.Invoke(null, null));
    }

    [Fact]
    public void TopLevelFunction_NameShadowedByEnclosingClassField_PartialAppliedInNestedObjectSuper()
    {
        // Regression (fuzzer seed 0x4fd43693): a top-level function whose name
        // happens to match a class field on an enclosing scope (here `f0` is
        // both a top-level function and a field of FCls_0) was being captured
        // as if it were the field when referenced from a nested object's
        // (super ...) args. EmitObjectExpr's free-var resolution found the
        // class field first and recorded the capture's CIL signature as the
        // field's type (Int) while the recovered ZType remained the function
        // type — the inner ctor parameter ended up typed Int but the closure
        // lambda's capture field was typed Func, so the synthesized
        // construction `ldarg <int>; stfld <Func>` produced
        // `[StackUnexpected][found Int32][expected ref 'Func`3<...>']`.
        //
        // EmitLambda has the same shape of bug for the `<>this` capture path:
        // any free var that names a class field flips `needsThisCapture` on,
        // even when the var really resolves to a top-level static method via
        // EmitCall. When that lambda is emitted inside a nested object's
        // ctor (where `ldarg.0` is a different object's `this`), the
        // synthesized `ldarg.0; stfld <>this` flows the wrong-typed `this`
        // into the enclosing-class-typed `<>this` field.
        //
        // The fix in both spots: when a free var would resolve to a class
        // field but its recovered ZType is a function and there is a
        // top-level method or static field of the same name, skip the
        // capture — EmitCall routes through `_methods` / `_staticFields`
        // directly from anywhere in the assembly.
        var source =
            @"(namespace Repro)
(module test)

(define (f0 [x : (Int -> Int)] [y : Int]) : Int
  (x y))

(define-class #:open FCls_0
  [f0 : Int #:mutable]
  [f1 : Int #:mutable]
  [f2 : Int #:mutable]
  (define (M0_0 [p0 : Int]) : Int p0))

(define (compute) : Int
  (let ([outer (object : FCls_0
                (constructor (super 1 2 3))
                (define (M0_0 [p0 : Int]) : Int
                  (let ([inner (object : FCls_0
                                (constructor
                                  (super 0 0 ((partial f0 (lambda ([x : Int]) x)) p0)))
                                (define (M0_0 [p0 : Int]) : Int p0))])
                    p0)))])
    42))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // The compute body throws away the inner object, so the call only
        // exercises type emission. Pre-fix this throws InvalidProgramException
        // at JIT time because the IL is not verifiable; post-fix it returns 42.
        Assert.Equal(42, compute.Invoke(null, null));
    }

    // Regression: the IL backend used to lower (and a b) and (or a b) to the
    // bitwise `and`/`or` opcodes, which evaluate both operands eagerly. The C#
    // backend already used `&&` / `||`, so a fuzz case that wrote
    //   (and #f (begin (Math.Abs int.MinValue) #t))
    // returned `false` under C# but threw OverflowException under IL. The
    // tests below pin short-circuit semantics: the right operand must not
    // execute when the left operand is decisive.
    [Fact]
    public void And_DoesNotEvaluateRightOperand_WhenLeftIsFalse_Il()
    {
        var source =
            @"(module test)
(import-clr [abs System.Math/Abs : (Int -> Int)])
(define (compute) : Int
  (if (and #f (begin (abs -2147483648) #t)) 1 2))";
        Assert.Equal(2, RunComputeOnIl(source));
    }

    [Fact]
    public void Or_DoesNotEvaluateRightOperand_WhenLeftIsTrue_Il()
    {
        var source =
            @"(module test)
(import-clr [abs System.Math/Abs : (Int -> Int)])
(define (compute) : Int
  (if (or #t (begin (abs -2147483648) #f)) 1 2))";
        Assert.Equal(1, RunComputeOnIl(source));
    }

    // Regression (fuzz seeds 0xa2b92d32, 0x0fb0d02f): the IL backend's
    // WithHandlersHoister A-normalized both operands of `and`/`or` BinOps
    // whenever any operand contained a transitive `with-handlers`. Lifting an
    // operand into a `Let` evaluates it unconditionally and so eagerly ran the
    // right-hand side, defeating short-circuit semantics. The fuzzer
    // surfaced this as a divergence: programs of the shape
    //   (or (... with-handlers ...) (... duplicate-key hash ...))
    // returned cleanly under the C# backend (`||` short-circuits) but threw
    // ArgumentException under IL because the duplicate-key hash in the
    // dead-by-construction right operand was being executed anyway.
    [Fact]
    public void Or_ShortCircuits_WhenLeftOperandContainsWithHandlers_Il()
    {
        var source =
            @"(module test)
(import-clr [abs System.Math/Abs : (Int -> Int)])
(define (compute) : Int
  (if (or (with-handlers ([System.InvalidOperationException ex] #t) #t)
          (begin (abs -2147483648) #f))
    1 2))";
        Assert.Equal(1, RunComputeOnIl(source));
    }

    [Fact]
    public void And_ShortCircuits_WhenLeftOperandContainsWithHandlers_Il()
    {
        var source =
            @"(module test)
(import-clr [abs System.Math/Abs : (Int -> Int)])
(define (compute) : Int
  (if (and (with-handlers ([System.InvalidOperationException ex] #t) #f)
           (begin (abs -2147483648) #t))
    1 2))";
        Assert.Equal(2, RunComputeOnIl(source));
    }

    [Fact]
    public void Or_NestedShortCircuit_WithHandlersInInnerLeft_Il()
    {
        // Mirrors the original fuzz failure shape: the with-handlers is
        // buried inside a left operand of an inner `or`, and the side-
        // effecting expression is the right operand of the outer `or`.
        // The inner `or` returns true via its left side (#t), so the outer
        // `or` must short-circuit before the right operand executes.
        var source =
            @"(module test)
(import-clr [abs System.Math/Abs : (Int -> Int)])
(define (compute) : Int
  (if (or (or #t (with-handlers ([System.InvalidOperationException ex] #t) #t))
          (begin (abs -2147483648) #f))
    1 2))";
        Assert.Equal(1, RunComputeOnIl(source));
    }

    private static int RunComputeOnIl(string source)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        return (int)compute.Invoke(null, null)!;
    }

    // Regression (fuzz seeds 0xdffe5110, 0x12acad76 and others): `define-async`
    // bodies that placed an `await` inside `with-handlers` produced an
    // unverifiable async state machine. The MoveNext jump table was emitted
    // before the inner try region but its switch targets (the resume labels)
    // landed inside that region, so ilverify rejected the IL with
    // `BranchIntoTry` and the JIT refused to run it. The fix is a cascading
    // dispatch: the outer switch jumps to a trampoline placed just before the
    // inner try; execution then falls through into the try, where a per-
    // with-handlers dispatch routes to the actual resume label without
    // crossing a try boundary.
    [Fact]
    public void AsyncAwaitInsideWithHandlers_TryBodyResumePath_Il()
    {
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int)
  (if (> x 0) x (raise (new System.InvalidOperationException ""fail""))))

(define-async (compute) : (Task Int)
  (with-handlers ([System.InvalidOperationException ex] -1)
    (await (g0 7))))";
        Assert.Equal(7, RunAsyncComputeOnIl(source));
    }

    [Fact]
    public void AsyncAwaitInsideWithHandlers_HandlerCatchesAwaitedException_Il()
    {
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int)
  (if (> x 0) x (raise (new System.InvalidOperationException ""fail""))))

(define-async (compute) : (Task Int)
  (with-handlers ([System.InvalidOperationException ex] -1)
    (await (g0 0))))";
        Assert.Equal(-1, RunAsyncComputeOnIl(source));
    }

    [Fact]
    public void AsyncAwaitsBothInsideAndOutsideWithHandlers_Il()
    {
        // Mixed: one await before the with-handlers (top-level dispatch),
        // and one inside it (cascading dispatch). The outer state-machine
        // dispatch must route the second state through the trampoline while
        // routing the first directly to its resume label.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)

(define-async (compute) : (Task Int)
  (let ([a (await (g0 5))])
    (with-handlers ([System.InvalidOperationException ex] -1)
      (let ([b (await (g0 11))])
        (+ a b)))))";
        Assert.Equal(16, RunAsyncComputeOnIl(source));
    }

    [Fact]
    public void AsyncTwoAwaitsInsideSameWithHandlers_Il()
    {
        // Two await points inside the same try body produce two resume
        // labels in the inner region; the inner dispatch must contain
        // entries for both states.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)

(define-async (compute) : (Task Int)
  (with-handlers ([System.InvalidOperationException ex] -1)
    (let ([a (await (g0 3))])
      (let ([b (await (g0 4))])
        (+ a b)))))";
        Assert.Equal(7, RunAsyncComputeOnIl(source));
    }

    [Fact]
    public void AsyncAwaitInsideNestedWithHandlers_Il()
    {
        // Two levels of with-handlers nested around an await. The cascade
        // must traverse: outer dispatch -> outer trampoline -> outer
        // with-handlers' dispatch -> inner trampoline -> inner with-handlers'
        // dispatch -> resume label.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)

(define-async (compute) : (Task Int)
  (with-handlers ([System.Exception e1] -2)
    (with-handlers ([System.InvalidOperationException e2] -1)
      (await (g0 9)))))";
        Assert.Equal(9, RunAsyncComputeOnIl(source));
    }

    [Fact]
    public void AsyncNestedAwaitInsideAwaitedExpr_Il()
    {
        // Regression (fuzz seed 0x73fe9f16): when an outer (await X)'s X
        // contains a nested (await Y), AsyncStateMachineAnalyzer.CollectInfo
        // did not recurse into awaitNode.Expr, so only the outer await was
        // counted as an AwaitPoint. The IL emitter still walked into the
        // nested await (because EmitMoveNextAwait emits Expr first) and
        // looked up AwaiterFields[stateNum] for a state number the analyzer
        // never registered, throwing KeyNotFoundException.
        var source =
            @"(module test)
(define-async (g [x : Int]) : (Task Int) x)
(define-async (compute) : (Task Int)
  (await (g (await (g 21)))))";
        Assert.Equal(21, RunAsyncComputeOnIl(source));
    }

    [Fact]
    public void AsyncNestedAwaitWithSiblingAwaitInIfBranch_Il()
    {
        // Closer to the original fuzz failure shape: an `if` with a nested-
        // await call in the then-branch and a sibling await in the else-
        // branch. With the old analyzer the nested await was not counted,
        // so only one awaiter field was created; the second emitted await
        // (the sibling in the other branch) overflowed the dictionary.
        var source =
            @"(module test)
(define-async (g [x : Int]) : (Task Int) x)
(define-async (compute) : (Task Int)
  (if #f
      (await (g (await (g 1))))
      (await (g 42))))";
        Assert.Equal(42, RunAsyncComputeOnIl(source));
    }

    // Regression (fuzz seed 0xf20aef72): when an `await` appears as a non-first
    // operand in a surrounding expression (e.g. the second argument of a call,
    // the right operand of a BinOp), the IL state-machine lowering emitted the
    // suspend/resume sequence with operands from the surrounding expression
    // still on the evaluation stack. The IsCompleted=true fall-through path
    // arrived at the GetResult call with stack height N; the resume path
    // (entered via the MoveNext switch table) arrived at the same instruction
    // with stack height 0. AsmResolver's CilMaxStackCalculator detected the
    // mismatch and threw `StackImbalanceException` at PE write time.
    //
    // Fix: AwaitHoister A-normalizes any compound expression containing an
    // `await` into top-level `let` bindings so every suspension point has an
    // empty evaluation stack — same approach as WithHandlersHoister for
    // try-block entry.
    [Fact]
    public void AsyncAwaitAsSecondArgOfSyncCall_Il()
    {
        // `(h0 a (await ...))` — `a` was on the stack when the await fired.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)
(define (h0 [x : Int] [y : Int]) : Int (+ x y))
(define-async (compute) : (Task Int)
  (h0 10 (await (g0 32))))";
        Assert.Equal(42, RunAsyncComputeOnIl(source));
    }

    [Fact]
    public void AsyncAwaitAsSecondArgOfAsyncCall_Il()
    {
        // `(await (g0 a (await (g0 b 1))))` — same shape that the fuzzer hit
        // first, with both calls into async helpers.
        var source =
            @"(module test)
(define-async (g0 [x : Int] [y : Int]) : (Task Int) (+ x y))
(define-async (compute) : (Task Int)
  (await (g0 5 (await (g0 30 7)))))";
        Assert.Equal(42, RunAsyncComputeOnIl(source));
    }

    [Fact]
    public void AsyncAwaitOnRightOfBinOp_Il()
    {
        // BinOp emits Left then Right; with Left on the stack the await on
        // Right tripped the same imbalance.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)
(define-async (compute) : (Task Int)
  (+ 10 (await (g0 32))))";
        Assert.Equal(42, RunAsyncComputeOnIl(source));
    }

    [Fact]
    public void AsyncAwaitAsLaterArgOfThreeArgCall_Il()
    {
        // Stack height 2 at the await point: two prior args were pushed.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)
(define (sum3 [a : Int] [b : Int] [c : Int]) : Int (+ (+ a b) c))
(define-async (compute) : (Task Int)
  (sum3 10 20 (await (g0 12))))";
        Assert.Equal(42, RunAsyncComputeOnIl(source));
    }

    private static int RunAsyncComputeOnIl(string source)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        var task = (Task<int>)compute.Invoke(null, null)!;
        return task.GetAwaiter().GetResult();
    }

    // Regression (fuzz seeds 0xfa8453e4, 0xe7682fe4, 0x0092619c and others):
    // `(begin a b ... last)` desugars to nested `(let ([_ a]) (let ([_ b]) ...
    // last))`. When several such sequences appear inside an async function
    // and the analyzer walks them in lexical order (i.e. they are not
    // tucked inside an `await`'s argument, which the analyzer does not
    // recurse through), the state-machine analyzer saw multiple `_` Lets
    // and deduped them by name. A single hoisted field of the *first* `_`
    // Let's value type was created and reused for every subsequent `_` Let
    // in the function. If a later `_` Let bound a value of a different
    // type, the IL emitter still issued a `stfld` against the same field
    // (e.g. Float into Int32): ilverify reported
    // `[found Double][expected Int32]` and on certain combinations the JIT
    // threw `InvalidProgramException`. The fix is to skip hoisting `_`
    // bindings entirely — they are never read, so they need not survive
    // across awaits. The Stloc to a fresh local is still emitted, so the
    // discarded expressions still run for their side effects.
    //
    // The runtime CLR is lenient about Float-vs-Int32 stfld (both are 4
    // bytes and the field is dead) so a pure `Assembly.Load + Invoke`
    // round-trip can pass even on broken IL. These tests inspect the
    // emitted state-machine type via `System.Reflection.Metadata` to
    // assert that no `<_>5__` field is hoisted, while also confirming the
    // method runs and returns the expected value.

    [Fact]
    public void AsyncBeginsWithDifferentDiscardTypes_DoNotAliasUnderscoreField_Il()
    {
        // First begin makes the analyzer's first `_` Let an Int (would
        // type the shared field as Int32). A second begin nested inside
        // discards a Float — without the fix, that begin's `stfld` lands
        // a Float in the shared Int32 field.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)

(define-async (compute) : (Task Int)
  (begin 1 2
    (let ([a (await (g0 5))])
      (begin 3.14
        (+ a 7)))))";
        var bytes = CompileToIlBytes(source);
        AssertNoUnderscoreStateMachineField(bytes);
        Assert.Equal(12, RunAsyncComputeFromBytes(bytes));
    }

    [Fact]
    public void AsyncBeginsWithBoolThenFloatDiscards_Il()
    {
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)

(define-async (compute) : (Task Int)
  (begin #t #f
    (let ([a (await (g0 5))])
      (begin -2.5
        (+ a 4)))))";
        var bytes = CompileToIlBytes(source);
        AssertNoUnderscoreStateMachineField(bytes);
        Assert.Equal(9, RunAsyncComputeFromBytes(bytes));
    }

    [Fact]
    public void AsyncBeginsWithStringThenIntDiscards_Il()
    {
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)

(define-async (compute) : (Task Int)
  (begin ""hello"" ""world""
    (let ([a (await (g0 10))])
      (begin 99
        (+ a 3)))))";
        var bytes = CompileToIlBytes(source);
        AssertNoUnderscoreStateMachineField(bytes);
        Assert.Equal(13, RunAsyncComputeFromBytes(bytes));
    }

    [Fact]
    public void AsyncBeginsAcrossWithHandlersBoundary_Il()
    {
        // Begin discards interleaved with the cascading-dispatch path from
        // the prior fix. Both fixes must be engaged simultaneously.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)

(define-async (compute) : (Task Int)
  (begin 100 200
    (with-handlers ([System.InvalidOperationException e] -1)
      (let ([a (await (g0 6))])
        (begin -9.5
          (+ a 1))))))";
        var bytes = CompileToIlBytes(source);
        AssertNoUnderscoreStateMachineField(bytes);
        Assert.Equal(7, RunAsyncComputeFromBytes(bytes));
    }

    // The fuzzer (seed 0x9358a064, case 0x0659cd79) generated an async
    // function whose `with-handlers` had an `await` inside a catch handler.
    // The IL emitter put the handler body — and therefore the await's resume
    // label — *inside* the catch region, while the top-of-MoveNext state
    // dispatch sat outside the try, so the dispatch's `switch` jumped into
    // a protected handler and ilverify rejected the assembly with
    // "[BranchIntoHandler] Branch into exception handler block."
    //
    // Fix: when emitting `with-handlers` whose handler bodies contain an
    // `await` inside an async method, lift the handler bodies out of the
    // catch. The catch only stores the caught exception (or pops it) and
    // writes a tag local; the handler bodies run in the regular code path
    // after the try region, where the resume labels are reachable from
    // the outer dispatch.

    [Fact]
    public void AsyncWithHandlersAwaitInHandler_Il_Verifies()
    {
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)

(define-async (compute) : (Task Int)
  (with-handlers ([System.Exception e] (await (g0 42)))
    7))";
        var bytes = CompileToIlBytes(source);
        Assert.Equal(7, RunAsyncComputeFromBytes(bytes));
    }

    [Fact]
    public void AsyncWithHandlersAwaitInHandler_RunsHandlerOnThrow_Il()
    {
        // Body raises; the catch fires and runs `await (g0 42)` to compute
        // the result. Without the lift the IL would not even verify.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) (+ x 100))

(define-async (compute) : (Task Int)
  (with-handlers ([System.Exception e] (await (g0 42)))
    (raise (new System.InvalidOperationException ""boom""))))";
        var bytes = CompileToIlBytes(source);
        Assert.Equal(142, RunAsyncComputeFromBytes(bytes));
    }

    [Fact]
    public void AsyncWithHandlersAwaitInHandler_BoundExceptionUsedAfterAwait_Il()
    {
        // The handler binding `e` must survive the await — the analyzer
        // hoists it to a state-machine field, the IL emitter persists the
        // captured exception to that field, and after the await the field
        // is restored so the post-await read sees the right exception.
        var source =
            @"(module test)
(import-clr
  [exn-msg System.Exception.Message :instance-property : (System.Exception -> String)])

(define-async (g0 [x : Int]) : (Task Int) x)

(define-async (compute) : (Task Int)
  (with-handlers ([System.Exception e]
    (let ([base (await (g0 1000))])
      (if (= (exn-msg e) ""boom"") (+ base 5) base)))
    (raise (new System.InvalidOperationException ""boom""))))";
        var bytes = CompileToIlBytes(source);
        Assert.Equal(1005, RunAsyncComputeFromBytes(bytes));
    }

    [Fact]
    public void AsyncWithHandlersAwaitInHandler_DiscardBinding_Il()
    {
        // `_` binding: the catch must `pop` the exception (not store it)
        // before tagging and leaving. Verifies the discard branch of the
        // lifted-catch emit path.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) (+ x 1))

(define-async (compute) : (Task Int)
  (with-handlers ([System.Exception _] (await (g0 9)))
    (raise (new System.InvalidOperationException ""boom""))))";
        var bytes = CompileToIlBytes(source);
        Assert.Equal(10, RunAsyncComputeFromBytes(bytes));
    }

    [Fact]
    public void AsyncWithHandlersAwaitInHandler_MultipleHandlersDispatchByType_Il()
    {
        // Multiple handlers, each with its own await. The lift must build
        // a tag dispatch that picks the *right* handler based on which
        // catch matched. Here the body raises ArithmeticException, so only
        // the second handler should run and contribute its async result.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) x)

(define-async (compute) : (Task Int)
  (with-handlers
    ([System.InvalidOperationException _] (await (g0 11)))
    ([System.ArithmeticException _] (await (g0 22)))
    ([System.Exception _] (await (g0 33)))
    (raise (new System.DivideByZeroException ""bad math""))))";
        var bytes = CompileToIlBytes(source);
        // DivideByZeroException is a subclass of ArithmeticException → 22.
        Assert.Equal(22, RunAsyncComputeFromBytes(bytes));
    }

    [Fact]
    public void AsyncWithHandlersAwaitInHandler_NoExceptionUsesBody_Il()
    {
        // When the body succeeds, the lift must surface the body's value
        // (not run any handler). Tag local stays zero and dispatch jumps
        // straight to the end with `resultLocal` already populated.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) (* x 2))

(define-async (compute) : (Task Int)
  (with-handlers ([System.Exception _] (await (g0 100)))
    (+ 1 2)))";
        var bytes = CompileToIlBytes(source);
        Assert.Equal(3, RunAsyncComputeFromBytes(bytes));
    }

    [Fact]
    public void AsyncWithHandlersAwaitInBothBodyAndHandler_Il()
    {
        // Body uses an await (exercises the existing trampoline / per-WH
        // dispatch) AND the handler uses an await (exercises the new
        // catch-lift). Both code paths coexist in one with-handlers.
        var source =
            @"(module test)
(define-async (g0 [x : Int]) : (Task Int) (+ x 10))

(define-async (compute) : (Task Int)
  (with-handlers ([System.Exception _] (await (g0 200)))
    (await (g0 5))))";
        var bytes = CompileToIlBytes(source);
        Assert.Equal(15, RunAsyncComputeFromBytes(bytes));
    }

    private static byte[] CompileToIlBytes(string source)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return ((CompilationResult.IlOutputResult)result).OutputBytes;
    }

    private static int RunAsyncComputeFromBytes(byte[] bytes)
    {
        var asm = Assembly.Load(bytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        var task = (Task<int>)compute.Invoke(null, null)!;
        return task.GetAwaiter().GetResult();
    }

    // Regression: when a stdlib generic function with no inferable parameter context
    // is called (e.g., `(concurrent-dictionary/new)`, whose K/V are determined only
    // by the return type), the C# emitter must emit explicit type arguments. The
    // call commonly lands in a position where Roslyn can't reverse-flow the target
    // type — Func<T,...> casts around immediately-invoked lambdas, the inside of a
    // ternary arm, etc. — and would fail with CS0411 ("type arguments cannot be
    // inferred from the usage"). Found by the fuzzer.
    [Fact]
    public void GenericZeroArgCall_EmitsExplicitTypeArgs()
    {
        var source =
            @"(module test)
(import stdlib/concurrent/dictionary)

(define (compute) : Int
  (let ([d (concurrent-dictionary/new)])
    (begin
      (put! d 0 42)
      (length d))))";
        var cs = Compile(source);
        Assert.Contains("ConcurrentDictionary_New<int, int>()", cs);
        Assert.Contains("Put_b<int, int>(", cs);
        Assert.Contains("Length<int, int>(", cs);
    }

    // Regression: even when the result is consumed via an immediately-invoked
    // lambda (which the emitter generates for several let/begin lowerings), the
    // generic stdlib call must carry its inferred type arguments at the call site,
    // because the lambda parameter type doesn't propagate back into method-type
    // inference.
    [Fact]
    public void GenericCallInsideLambdaCast_EmitsExplicitTypeArgs()
    {
        var source =
            @"(module test)
(import stdlib/concurrent/dictionary)

(define (compute) : Int
  (let ([d (let ([t (concurrent-dictionary/new)])
            (begin (put! t 0 42) t))])
    (length d)))";
        var cs = Compile(source);
        Assert.Contains("ConcurrentDictionary_New<int, int>()", cs);
    }

    [Fact]
    public void ObjectExpr_CapturesPatternVarWithFreeTypeParam_FieldTypeMatchesUseSites()
    {
        // Regression (fuzzer seed 0x4aa4c66f, case 0x2416d7a4): an `object`
        // expression captured a pattern variable bound from a generic union
        // case whose declaring type parameter was never pinned by the
        // construction site. The capture's `ZType` survived as a free
        // `ZTypeVar`, which `TypeToCs` emitted as `object` for the anonymous
        // class's backing field and ctor parameter. Every other emission of
        // the same free var (the `Lt<int, int>(var x26)` pattern, the Ok/Err
        // ctor invocations consuming the captured value) routes through
        // `FormatTypeArgs`, which substitutes `int` for free params. Roslyn
        // then rejected the resulting `Ok<int, string>(this.X26_field)` call
        // with CS1503: cannot convert from `object` to `int`.
        //
        // The fix mirrors `FormatTypeArgs`'s defaulting in `EmitObjectExpr`:
        // free type vars in captured types collapse to `int` before they
        // reach the field/ctor-param emission, keeping every site agreeing.
        var source =
            @"(module test)
(import stdlib/result)

(define-union (Either ^a ^b) (Lt [lv : ^a]) (Rt [rv : ^b]))

(define-class #:open Base
  [f0 : Int #:mutable]
  (define (M0 [p : Int]) : Int p))

(define (compute) : Int
  (match (Rt 1)
    [(Lt x26) (let ([x27 (object : Base
                          (constructor (super 1))
                          (define (M0 [p : Int]) : Int
                            (let ([x29 : (Result Int String) (Ok 24)])
                              (match (flat-map x29 (lambda ([x30 : Int]) (Ok x26)))
                                [(Ok x31) p]
                                [(Err _) 60]))))])
                (let ([x32 x26]) 0))]
    [(Rt _) 0]))";
        var cs = Compile(source);
        // The capture's field must be `int`, not `object`. If it regresses,
        // Roslyn rejects the generated source with CS1503.
        Assert.Contains("public int X26 { get; }", cs);
        Assert.Contains("public __Object_0(int x26)", cs);
        Assert.DoesNotContain("object X26", cs);
    }

    private static void AssertNoUnderscoreStateMachineField(byte[] bytes)
    {
        // The bug surfaced as a hoisted state-machine field named `<_>5__`.
        // After the fix, no such field exists on any nested state-machine
        // type, regardless of how many `(begin ...)` discards appeared in
        // the async body.
        using var pe = new PEReader(ImmutableArray.Create(bytes));
        var md = pe.GetMetadataReader();
        foreach (var fh in md.FieldDefinitions)
        {
            var f = md.GetFieldDefinition(fh);
            var name = md.GetString(f.Name);
            Assert.False(
                name == "<_>5__" || name.StartsWith("<_>5__", StringComparison.Ordinal),
                $"Hoisted underscore field detected: {name}"
            );
        }
    }

    // === Variadic operators: end-to-end execution ===
    //
    // These compile a single arity-0 function `compute` to IL, load it, and
    // invoke it. They prove the AST-level expansion produces correct runtime
    // behavior (left-fold for arithmetic, chained `and` for comparisons,
    // all-distinct for `!=`, right-fold for `and`/`or`, unary negate/invert
    // for 1-arg `-`/`/`).

    private static object? RunCompute(string body, string returnType = "Int")
    {
        var source = $"(module test)\n(define (compute) : {returnType} {body})";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        return method.Invoke(null, null);
    }

    [Fact]
    public void ImmediatelyInvokedLambda_ComputesCorrectly_Il()
    {
        // The IIFE beta-reduces into a let spine; the IL backend emits plain locals.
        Assert.Equal(3, RunCompute("((lambda ([x : Int] [y : Int]) (+ x y)) 1 2)"));
    }

    [Fact]
    public void NestedImmediatelyInvokedLambda_ComputesCorrectly_Il()
    {
        Assert.Equal(
            12,
            RunCompute("((lambda ([x : Int]) (* x 2)) ((lambda ([y : Int]) (+ y 1)) 5))")
        );
    }

    [Fact]
    public void LetInClassMethod_ComputesCorrectly_Il()
    {
        var source =
            @"(module test)
(define-class Box
  [v : Int]
  (define (Bump) : Int
    (let ([hello 5]) (+ hello 1))))
(define (compute) : Int
  (let ([b (new Box 0)])
    (Box/Bump b)))";
        Assert.Equal(6, RunComputeOnIl(source));
    }

    [Fact]
    public void VariadicAdd_FiveInts()
    {
        Assert.Equal(15, RunCompute("(+ 1 2 3 4 5)"));
    }

    [Fact]
    public void VariadicMul_ThreeInts()
    {
        Assert.Equal(24, RunCompute("(* 2 3 4)"));
    }

    [Fact]
    public void VariadicSub_LeftFold()
    {
        // ((100 - 10) - 5) - 2 = 83. Right-fold would yield 100 - (10 - (5 - 2)) = 93.
        Assert.Equal(83, RunCompute("(- 100 10 5 2)"));
    }

    [Fact]
    public void VariadicDiv_LeftFold()
    {
        // ((100 / 2) / 5) = 10. Right-fold would yield 100 / (2 / 5) = 100 / 0 = exception.
        Assert.Equal(10, RunCompute("(/ 100 2 5)"));
    }

    [Fact]
    public void VariadicAdd_TenOnes()
    {
        Assert.Equal(10, RunCompute("(+ 1 1 1 1 1 1 1 1 1 1)"));
    }

    [Fact]
    public void VariadicMixed()
    {
        // 1 + 2 + (3*4) + (10-5) = 1 + 2 + 12 + 5 = 20
        Assert.Equal(20, RunCompute("(+ 1 2 (* 3 4) (- 10 5))"));
    }

    [Fact]
    public void VariadicAdd_Floats()
    {
        Assert.Equal(6.0f, RunCompute("(+ 1.0 2.0 3.0)", "Float"));
    }

    [Fact]
    public void UnaryNegate_Int()
    {
        Assert.Equal(-7, RunCompute("(- 7)"));
    }

    [Fact]
    public void UnaryNegate_Float()
    {
        Assert.Equal(-7.5f, RunCompute("(- 7.5)", "Float"));
    }

    [Fact]
    public void UnaryInvert_Float()
    {
        Assert.Equal(0.25f, RunCompute("(/ 4.0)", "Float"));
    }

    [Fact]
    public void UnaryNegate_OfExpr()
    {
        // (- (* 3 4)) = -12
        Assert.Equal(-12, RunCompute("(- (* 3 4))"));
    }

    [Fact]
    public void VariadicLess_StrictlyAscending()
    {
        Assert.Equal(true, RunCompute("(< 1 2 3 4)", "Bool"));
    }

    [Fact]
    public void VariadicLess_OutOfOrder()
    {
        Assert.Equal(false, RunCompute("(< 1 3 2 4)", "Bool"));
    }

    [Fact]
    public void VariadicLess_StrictlyRejectsEqual()
    {
        // Strict `<` — equal middle elements break the chain.
        Assert.Equal(false, RunCompute("(< 1 2 2 3)", "Bool"));
    }

    [Fact]
    public void VariadicLessEq_AllowsEqual()
    {
        Assert.Equal(true, RunCompute("(<= 1 2 2 3)", "Bool"));
    }

    [Fact]
    public void VariadicGreater_StrictlyDescending()
    {
        Assert.Equal(true, RunCompute("(> 4 3 2 1)", "Bool"));
    }

    [Fact]
    public void VariadicGreaterEq_AllowsEqual()
    {
        Assert.Equal(true, RunCompute("(>= 4 3 3 1)", "Bool"));
    }

    [Fact]
    public void VariadicEq_AllEqual()
    {
        Assert.Equal(true, RunCompute("(= 5 5 5)", "Bool"));
    }

    [Fact]
    public void VariadicEq_OneDifferent()
    {
        Assert.Equal(false, RunCompute("(= 5 5 6)", "Bool"));
    }

    [Fact]
    public void VariadicNeq_AllDifferent()
    {
        Assert.Equal(true, RunCompute("(!= 1 2 3)", "Bool"));
    }

    [Fact]
    public void VariadicNeq_PairwiseDistinctButNotAllUnique()
    {
        // (!= 1 2 1) — pairs are (1,2)=true, (1,1)=false, (2,1)=true.
        // All-distinct semantics requires NO pair to be equal, so this is false.
        // A naive pairwise-chain would have given true. This test pins the
        // CL-style "all-distinct" interpretation.
        Assert.Equal(false, RunCompute("(!= 1 2 1)", "Bool"));
    }

    [Fact]
    public void VariadicNeq_TwoEqual()
    {
        Assert.Equal(false, RunCompute("(!= 1 1)", "Bool"));
    }

    [Fact]
    public void VariadicAnd_AllTrue()
    {
        Assert.Equal(true, RunCompute("(and #t #t #t)", "Bool"));
    }

    [Fact]
    public void VariadicAnd_OneFalse()
    {
        Assert.Equal(false, RunCompute("(and #t #f #t)", "Bool"));
    }

    [Fact]
    public void VariadicOr_AnyTrue()
    {
        Assert.Equal(true, RunCompute("(or #f #f #t)", "Bool"));
    }

    [Fact]
    public void VariadicOr_AllFalse()
    {
        Assert.Equal(false, RunCompute("(or #f #f #f)", "Bool"));
    }

    [Fact]
    public void VariadicCmp_NameMiddleArg_BothCompared()
    {
        // Confirms `(< 1 x 5)` with `x=3` evaluates correctly without
        // triggering the let-binding path (names are pure-repeatable).
        var src = "(let ([x 3]) (< 1 x 5))";
        Assert.Equal(true, RunCompute(src, "Bool"));
    }

    [Fact]
    public void VariadicCmp_NameMiddleArg_FailsWhenOutOfRange()
    {
        var src = "(let ([x 7]) (< 1 x 5))";
        Assert.Equal(false, RunCompute(src, "Bool"));
    }

    [Fact]
    public void VariadicArith_DefinedFunction()
    {
        // 4-arg sum inside a function definition — verifies the AST expansion
        // composes with `define` and parameter binding.
        var source =
            @"(module test)
(define (sum4 [a : Int] [b : Int] [c : Int] [d : Int]) : Int (+ a b c d))
(define (compute) : Int (sum4 1 2 3 4))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(10, compute.Invoke(null, null));
    }

    [Fact]
    public void VariadicCmp_SideEffectingMiddleArg_EvaluatesOnce()
    {
        // Class with a mutable counter; `(<= 1 (incr c) 100)` must call `incr`
        // exactly once even though the middle arg appears in two pairs of the
        // expanded `(and (<= 1 X) (<= X 100))`. The counter ends up at 1 if the
        // let-binding suppresses double-evaluation; at 2 if not.
        var source =
            @"(module test)
(define-class Counter
  [count : Int #:mutable]
  (constructor (set! count 0))
  (define (incr) : Int (begin (set! count (+ count 1)) count)))
(define (compute) : Int
  (let ([c (new Counter)])
    (let ([_ (<= 1 (Counter/incr c) 100)])
      (Counter/count c))))";
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        Assert.Equal(1, compute.Invoke(null, null));
    }

    // ─── Type Alias End-to-End ──────────────────────────────────
    // These tests verify that ZScheme type aliases resolve to the correct CLR types
    // in the generated C# code when compiling through the full pipeline.

    [Fact]
    public void EndToEnd_MutableHash_UsesDictionaryClrType()
    {
        var cs =
            @"(module test)
(import stdlib/mutable/hash)
(define (make-dict) : (Mutable-Hash String Int)
  (new (System.Collections.Generic.Dictionary String Int)))";
        var result = Compile(cs);
        Assert.Contains(
            "public static System.Collections.Generic.Dictionary<string, int> MakeDict()",
            result
        );
        Assert.Contains("return new System.Collections.Generic.Dictionary<string, int>();", result);
    }

    [Fact]
    public void EndToEnd_MutableList_UsesListClrType()
    {
        var cs =
            @"(module test)
(import stdlib/mutable/treelist)
(define (make-list) : (Mutable-TreeList Int)
  (new (System.Collections.Generic.List Int)))";
        var result = Compile(cs);
        Assert.Contains("public static System.Collections.Generic.List<int> MakeList()", result);
    }

    [Fact]
    public void EndToEnd_Hash_UsesImmutableDictionaryClrType()
    {
        var cs = Compile(
            @"(module test)
(import stdlib/hash)
(define (make-dict [d : (Hash String Int)]) : Unit
  ())"
        );
        Assert.Contains("System.Collections.Immutable.ImmutableDictionary<string, int> d", cs);
    }

    [Fact]
    public void EndToEnd_Vector_UsesImmutableArrayClrType()
    {
        var cs = Compile(
            @"(module test)
(import stdlib/vector)
(define (make-arr [v : (Vector Int)]) : Unit
  ())"
        );
        Assert.Contains("System.Collections.Immutable.ImmutableArray<int> v", cs);
    }

    [Fact]
    public void EndToEnd_List_UsesImmutableListClrType()
    {
        var cs = Compile(
            @"(module test)
(import stdlib/list)
(define (make-list [l : (List Int)]) : Unit
  ())"
        );
        Assert.Contains("Stdlib_ListModule.List<int> l", cs);
    }

    [Fact]
    public void EndToEnd_ConcurrentQueue_UsesConcurrentQueueClrType()
    {
        var cs =
            @"(module test)
(import stdlib/concurrent/queue)
(import-clr System.Collections.Concurrent)
(define (make-queue) : (Concurrent-Queue Int)
  (new (System.Collections.Concurrent.ConcurrentQueue Int)))";
        var result = Compile(cs);
        Assert.Contains(
            "public static System.Collections.Concurrent.ConcurrentQueue<int> MakeQueue()",
            result
        );
    }

    [Fact]
    public void EndToEnd_ConcurrentDictionary_UsesConcurrentDictionaryClrType()
    {
        var cs =
            @"(module test)
(import stdlib/concurrent/dictionary)
(import-clr System.Collections.Concurrent)
(define (make-dict) : (Concurrent-Dictionary String Int)
  (new (System.Collections.Concurrent.ConcurrentDictionary String Int)))";
        var result = Compile(cs);
        Assert.Contains(
            "public static System.Collections.Concurrent.ConcurrentDictionary<string, int> MakeDict()",
            result
        );
    }

    [Fact]
    public void EndToEnd_ConcurrentBag_UsesConcurrentBagClrType()
    {
        var cs =
            @"(module test)
(import stdlib/concurrent/bag)
(import-clr System.Collections.Concurrent)
(define (make-bag) : (Concurrent-Bag Int)
  (new (System.Collections.Concurrent.ConcurrentBag Int)))";
        var result = Compile(cs);
        Assert.Contains(
            "public static System.Collections.Concurrent.ConcurrentBag<int> MakeBag()",
            result
        );
    }

    [Fact]
    public void EndToEnd_ConcurrentStack_UsesConcurrentStackClrType()
    {
        var cs =
            @"(module test)
(import stdlib/concurrent/stack)
(import-clr System.Collections.Concurrent)
(define (make-stack) : (Concurrent-Stack Int)
  (new (System.Collections.Concurrent.ConcurrentStack Int)))";
        var result = Compile(cs);
        Assert.Contains(
            "public static System.Collections.Concurrent.ConcurrentStack<int> MakeStack()",
            result
        );
    }

    [Fact]
    public void EndToEnd_NestedAliases_ResolvesAllLevels()
    {
        var cs = Compile(
            @"(module test)
(import stdlib/mutable/hash)
(import stdlib/mutable/treelist)
(define (make-dict [d : (Mutable-Hash String (Mutable-TreeList Int))]) : Unit
  ())"
        );
        Assert.Contains(
            "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>> d",
            cs
        );
    }

    [Fact]
    public void EndToEnd_TypeAliasInFunctionParameter_UsesClrType()
    {
        var cs =
            @"(module test)
(import stdlib/mutable/treelist)
(define (add-item [lst : (Mutable-TreeList Int)] [x : Int]) : Unit
  ())";
        var result = Compile(cs);
        Assert.Contains(
            "public static void AddItem(System.Collections.Generic.List<int> lst, int x)",
            result
        );
    }

    [Fact]
    public void GenericFunctionReturningClosure_Il_VerifiesAndRuns()
    {
        // Regression (fuzzer seed 0xb008a828 and ~60 sibling cases, all routed
        // through stdlib `compose`): a closure created inside a *generic* method
        // captured the method's parameters, whose types mention the method's
        // generic parameters (`Func<!!0,!!1>`). The IL backend lifted the closure
        // into a NON-generic nested type whose fields and `Invoke` still referenced
        // `!!0/!!1/!!2`. A nested type may not reference its enclosing method's
        // generic parameters, so ilverify rejected the assembly with StackUnexpected
        // / DelegateCtor errors and the JIT threw InvalidProgramException on first
        // call.
        //
        // The fix mirrors the method's generic parameters onto the closure type,
        // rewrites the `!!i` references in its members to type parameters `!i`, and
        // instantiates the closure over the method's parameters at the construction
        // site (`closure<!!0,!!1,!!2>`).
        var source =
            @"(module test)
(import stdlib/core)
(define (compute) : Int
  (let ([f (compose (lambda ([x : Int]) (+ x 1))
                   (lambda ([y : Int]) (* y 2)))])
    (f 10)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // compose is left-to-right: (+ 10 1) = 11, then (* 11 2) = 22. Invoking
        // forces the JIT to verify `Compose` — the actual regression. Before the
        // fix this throws InvalidProgramException.
        Assert.Equal(22, compute.Invoke(null, null));
    }

    [Fact]
    public void GenericClosureCapturingMultipleTypeParams_Il_VerifiesAndRuns()
    {
        // Companion to the compose regression that exercises a user-defined generic
        // function returning a closure that captures parameters of two *distinct*
        // generic types (so the lifted closure type needs more than one mirrored
        // type parameter and the field/`Invoke` signatures interleave `!0` and `!1`).
        var source =
            @"(module test)
(define (make-adder [a : ^a] [b : ^b]) : (Int -> ^a)
  (lambda ([n : Int]) a))
(define (compute) : Int
  (let ([f (make-adder 7 ""ignored"")])
    (f 99)))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // The closure captures `a` (= 7) and returns it regardless of its arg.
        Assert.Equal(7, compute.Invoke(null, null));
    }

    // A generic-union field whose type parameter is pinned only by a delegate-typed
    // parameter must be captured into the delegate-adapter lambda at the delegate's
    // concrete leaf type, not left at the erased `object` representation. Previously
    // the unifier checked only delegate/function arity, so the `^b` of `(Left 7)`
    // (constrained to Int solely via `(delegate System.Func<int,int>)`) stayed an
    // unbound variable and defaulted to `object`. The IL backend then captured the
    // field as `object` and returned it from a lambda whose Invoke returns int32,
    // producing IL that ilverify rejects (StackUnexpected: found object, expected
    // Int32). The C# backend papered over it with a cast. Found by the fuzzer.
    [Fact]
    public void GenericUnionFieldThroughDelegateLambda_CapturedAsConcreteType_Il()
    {
        var source =
            @"(module test)
(define-union (FUn ^a ^b) (Left [lv : ^a]) (Right [rv : ^b]))
(define (run-func [f : (delegate System.Func<int,int>)]) : Int (f 10))
(define (go) : Int
  (match (Left 7)
    [(Left x) 0]
    [(Right y) (run-func (lambda ([z : Int]) y))]))";

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);

        // Locate the delegate-adapter lambda (its Invoke returns int32) and assert the
        // captured `y` field is int32, not object — the erased capture is exactly what
        // made the lambda body return `object` where the delegate expects `int32`.
        var closures = asm.GetTypes()
            .Where(t =>
                t.GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance) is { } inv
                && inv.ReturnType == typeof(int)
                && inv.GetParameters() is [{ ParameterType: var p }]
                && p == typeof(int)
            )
            .ToList();
        Assert.NotEmpty(closures);

        var captureFields = closures
            .SelectMany(t =>
                t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            )
            .ToList();
        Assert.NotEmpty(captureFields);
        Assert.All(captureFields, f => Assert.NotEqual(typeof(object), f.FieldType));
        Assert.Contains(captureFields, f => f.FieldType == typeof(int));
    }

    [Fact]
    public void EndToEnd_IlNestedLetSameName_RestoresOuterBindingAfterInnerScope()
    {
        // Regression: EmitLet bound `locals[name]` to the inner let's CIL local
        // and never restored the outer binding. After the inner `(let ([x 42]) x)`
        // sub-expression finished, the trailing reference to the *outer* `x`
        // resolved to the inner local, so the IL backend computed 42 + 42 = 84
        // instead of 42 + 7 = 49. The C# backend (block-scoped locals) was correct.
        var source =
            @"(module test)
(define (compute) : Int
  (let ([x 7])
    (+ (let ([x 42]) x) x)))";
        Assert.Equal(49, CompileIlAndRunInt(source));
    }

    [Fact]
    public void EndToEnd_IlNestedPartialApplication_DoesNotLeakInnerArgument()
    {
        // Regression: `partial` lowering reuses the synthetic parameter name `__p0`
        // for every partial. Nested partials therefore produced shadowing `let __p0`
        // bindings; because EmitLet did not restore the outer slot, the inner
        // partial's argument (-2147483648 in the fuzzer case, 42 here) leaked into
        // the outer call's parameter. f0 returns its second arg, so the correct
        // result is f0(f1(1,42)=0, 7) = 7, but the IL backend returned 42.
        var source =
            @"(module test)
(define (f0 [a : Int] [b : Int]) : Int b)
(define (f1 [a : Int] [b : Int]) : Int 0)
(define (compute) : Int
  ((partial f0 ((partial f1 1) 42)) 7))";
        Assert.Equal(7, CompileIlAndRunInt(source));
    }

    [Fact]
    public void EndToEnd_IlMatchVariablePatternShadow_RestoresOuterBindingAfterArm()
    {
        // Regression: EmitPatternTest binds a match arm's pattern variables into the
        // shared `locals` map without restoring the outer slot afterward (same bug
        // class as the nested-`let` leak). The arm `[x x]` binds `x` to the scrutinee
        // (42); after the match, the trailing reference to the *outer* `x` resolved to
        // that leaked pattern local, so the IL backend computed 42 + 42 = 84 instead
        // of 42 + 7 = 49. The arms must be scoped so the outer `x` (= 7) is restored.
        var source =
            @"(module test)
(define (ident [x : Int]) : Int x)
(define (compute) : Int
  (let ([x 7])
    (+ (match (ident 42) [x x]) x)))";
        Assert.Equal(49, CompileIlAndRunInt(source));
    }

    [Fact]
    public void EndToEnd_IlMatchTuplePatternShadow_RestoresOuterBindingAfterArm()
    {
        // Same leak via a destructuring (tuple) pattern: the arm binds both `x` and
        // `y`; without per-arm scoping the bound `x` (40) leaks past the match and
        // corrupts the trailing outer `x` (7). Correct: (40 + 2) + 7 = 49.
        var source =
            @"(module test)
(define (ident [x : Int]) : Int x)
(define (compute) : Int
  (let ([x 7])
    (+ (match (values (ident 40) (ident 2)) [(values x y) (+ x y)]) x)))";
        Assert.Equal(49, CompileIlAndRunInt(source));
    }

    // ---------------------------------------------------------------------
    // Constant integer `/` and `%` must throw at runtime on the .NET overflow
    // and divide-by-zero cases in BOTH backends. The IL backend always emits a
    // `div`/`rem` opcode (throws). The C# backend used to wrap constant-only
    // arithmetic in `unchecked(...)`, which let Roslyn const-fold
    // `int.MinValue / -1` to `int.MinValue` and `int.MinValue % -1` to `0`
    // (and reject `x / 0` as CS0020) — diverging from IL. Found by the fuzzer's
    // differential-exec oracle on `(% -2147483648 -1)`.
    // ---------------------------------------------------------------------

    [Fact]
    public void EndToEnd_ConstantIntMinValueDivByNegOne_ThrowsInBothBackends()
    {
        var source = "(module test)\n(define (compute) : Int (/ -2147483648 -1))";
        Assert.Throws<OverflowException>(() => CompileIlAndRunInt(source));
        Assert.Throws<OverflowException>(() => CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void EndToEnd_ConstantIntMinValueModByNegOne_ThrowsInBothBackends()
    {
        var source = "(module test)\n(define (compute) : Int (% -2147483648 -1))";
        Assert.Throws<OverflowException>(() => CompileIlAndRunInt(source));
        Assert.Throws<OverflowException>(() => CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void EndToEnd_ConstantDivByZero_ThrowsInBothBackends()
    {
        var source = "(module test)\n(define (compute) : Int (/ 5 0))";
        Assert.Throws<DivideByZeroException>(() => CompileIlAndRunInt(source));
        Assert.Throws<DivideByZeroException>(() => CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void EndToEnd_ConstantModByZero_ThrowsInBothBackends()
    {
        var source = "(module test)\n(define (compute) : Int (% 5 0))";
        Assert.Throws<DivideByZeroException>(() => CompileIlAndRunInt(source));
        Assert.Throws<DivideByZeroException>(() => CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void EndToEnd_ConstantNonOverflowingDivMod_AgreeAndReturnExpected()
    {
        // The runtime-forcing fix must not change ordinary constant arithmetic:
        // 100 / 7 = 14, 100 % 7 = 2, so the sum is 16 in both backends.
        var source = "(module test)\n(define (compute) : Int (+ (/ 100 7) (% 100 7)))";
        Assert.Equal(16, CompileIlAndRunInt(source));
        Assert.Equal(16, CompileCSharpAndRunInt(source));
    }

    // ---------------------------------------------------------------------
    // stdlib `when`/`unless` macros must select the right branch at RUNTIME.
    // Their bodies are Unit-typed, so we observe whether each ran by folding
    // distinct powers of ten into a mutable-vector accumulator and returning
    // it. true-tested clauses run, false-tested clauses are skipped:
    //   (when   (> 5 3) +1)    -> runs   (+1)
    //   (unless (> 3 5) +10)   -> runs   (+10)
    //   (when   (< 5 3) +100)  -> skips
    //   (unless (< 3 5) +1000) -> skips
    // so the accumulator is 11 in both backends.
    // ---------------------------------------------------------------------

    private const string WhenUnlessSource =
        @"(module test)
(import stdlib/control)
(import stdlib/vector)
(import stdlib/mutable/vector)
(define (compute) : Int
  (let ([acc (vector->mutable-vector (vector 0))])
    (begin
      (when   (> 5 3) (vector-set! acc 0 (+ (vector-ref acc 0) 1)))
      (unless (> 3 5) (vector-set! acc 0 (+ (vector-ref acc 0) 10)))
      (when   (< 5 3) (vector-set! acc 0 (+ (vector-ref acc 0) 100)))
      (unless (< 3 5) (vector-set! acc 0 (+ (vector-ref acc 0) 1000)))
      (vector-ref acc 0))))";

    [Fact]
    public void EndToEnd_WhenUnless_SelectsCorrectBranchesAtRuntime_Il()
    {
        Assert.Equal(11, CompileIlAndRunInt(WhenUnlessSource));
    }

    [Fact]
    public void EndToEnd_WhenUnless_SelectsCorrectBranchesAtRuntime_CSharp()
    {
        Assert.Equal(11, CompileCSharpAndRunInt(WhenUnlessSource));
    }

    [Fact]
    public void EndToEnd_When_RunsAllBodyExpressionsInOrder()
    {
        // A multi-expression `when` body must run every expression (not just the
        // last). Each clause adds a distinct digit; all three run -> 111.
        var source =
            @"(module test)
(import stdlib/control)
(import stdlib/vector)
(import stdlib/mutable/vector)
(define (compute) : Int
  (let ([acc (vector->mutable-vector (vector 0))])
    (begin
      (when #t
        (vector-set! acc 0 (+ (vector-ref acc 0) 1))
        (vector-set! acc 0 (+ (vector-ref acc 0) 10))
        (vector-set! acc 0 (+ (vector-ref acc 0) 100)))
      (vector-ref acc 0))))";
        Assert.Equal(111, CompileIlAndRunInt(source));
        Assert.Equal(111, CompileCSharpAndRunInt(source));
    }

    // ---- Identifier-collision disambiguation (EmitNameResolver) ----

    [Fact]
    public void NameCollision_FunctionVsPascalCase_BothBackendsAgree()
    {
        // `this-function` and `ThisFunction` both sanitize to `ThisFunction`; the
        // resolver must keep them distinct so both backends compute the same result.
        var source =
            @"(module test)
(define (this-function) : Int 10)
(define (ThisFunction) : Int 7)
(define (compute) : Int (- (this-function) (ThisFunction)))";
        Assert.Equal(3, CompileIlAndRunInt(source));
        Assert.Equal(3, CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void NameCollision_FunctionVsPascalCase_EmitsDistinctCSharpMethods()
    {
        var source =
            @"(module test)
(define (this-function) : Int 10)
(define (ThisFunction) : Int 7)
(define (compute) : Int (- (this-function) (ThisFunction)))";
        var cs = Compile(source);
        Assert.Contains("ThisFunction(", cs);
        Assert.Contains("ThisFunction_fn(", cs);
    }

    [Fact]
    public void NameCollision_TopLevelValues_BothBackendsAgree()
    {
        var source =
            @"(module test)
(define this-value 10)
(define ThisValue 7)
(define (compute) : Int (- this-value ThisValue))";
        Assert.Equal(3, CompileIlAndRunInt(source));
        Assert.Equal(3, CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void NameCollision_SpecialCharSuffix_BothBackendsAgree()
    {
        // `ready?` sanitizes to `Ready_q`, colliding with a literal `ready_q`.
        var source =
            @"(module test)
(define (ready?) : Int 10)
(define (ready_q) : Int 7)
(define (compute) : Int (- (ready?) (ready_q)))";
        Assert.Equal(3, CompileIlAndRunInt(source));
        Assert.Equal(3, CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void NameCollision_LocalBindings_BothBackendsAgree()
    {
        // Two sibling locals `this-var` and `ThisVar` both sanitize to `thisVar`;
        // the resolver alpha-renames the collider so neither backend miscompiles.
        var source =
            @"(module test)
(define (compute) : Int
  (let ([this-var 10])
    (let ([ThisVar 7])
      (- this-var ThisVar))))";
        Assert.Equal(3, CompileIlAndRunInt(source));
        Assert.Equal(3, CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void NameCollision_LambdaParams_BothBackendsAgree()
    {
        // Colliding parameter names across a lambda boundary.
        var source =
            @"(module test)
(define (apply2 [f : (Int -> Int)] [x : Int]) : Int (f x))
(define (compute) : Int
  (let ([this-var 100])
    (apply2 (lambda ([ThisVar : Int]) (- this-var ThisVar)) 7)))";
        Assert.Equal(93, CompileIlAndRunInt(source));
        Assert.Equal(93, CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void NameCollision_NoCollision_LeavesNamesUnchanged()
    {
        // Sanity: a non-colliding program is byte-for-byte unaffected (no `_fn`).
        var source =
            @"(module test)
(define (add [x : Int] [y : Int]) : Int (+ x y))
(define (compute) : Int (add 1 2))";
        var cs = Compile(source);
        Assert.DoesNotContain("_fn", cs);
        Assert.Equal(3, CompileIlAndRunInt(source));
        Assert.Equal(3, CompileCSharpAndRunInt(source));
    }

    // ---- Type-name collision disambiguation (EmitNameResolver) ----

    [Fact]
    public void TypeCollision_RecordVsRecord_BothBackendsAgree()
    {
        // `r` and `R` both sanitize to `R`; the resolver must keep the two record types
        // distinct so each constructor/accessor resolves to the right one.
        var source =
            @"(module test)
(define-record r [a : Int])
(define-record R [b : Int])
(define (compute) : Int (- (R/b (R 10)) (r/a (r 7))))";
        Assert.Equal(3, CompileIlAndRunInt(source));
        Assert.Equal(3, CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void TypeCollision_RecordVsRecord_EmitsDistinctCSharpTypes()
    {
        var source =
            @"(module test)
(define-record r [a : Int])
(define-record R [b : Int])
(define (compute) : Int (- (R/b (R 10)) (r/a (r 7))))";
        var cs = Compile(source);
        Assert.Contains("record R(", cs); // first claimant keeps the base name
        Assert.Contains("R_type", cs); // collider disambiguated
    }

    [Fact]
    public void TypeCollision_UnionCases_BothBackendsAgree()
    {
        // Cases `my-case` and `MyCase` (in distinct unions) both sanitize to `MyCase`;
        // construction and pattern matching must resolve each to its own case type.
        var source =
            @"(module test)
(define-union UA (my-case [v : Int]))
(define-union UB (MyCase [w : Int]))
(define (compute) : Int
  (- (match (MyCase 10) [(MyCase w) w])
     (match (my-case 7) [(my-case v) v])))";
        Assert.Equal(3, CompileIlAndRunInt(source));
        Assert.Equal(3, CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void TypeCollision_RecordVsValue_BothBackendsAgree()
    {
        // A record `counter` and a function `Counter` both sanitize to `Counter`; the type
        // keeps it and the value yields with `_fn` (separate suffix namespaces).
        var source =
            @"(module test)
(define-record counter [n : Int])
(define (Counter) : Int 7)
(define (compute) : Int (- (counter/n (counter 10)) (Counter)))";
        Assert.Equal(3, CompileIlAndRunInt(source));
        Assert.Equal(3, CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void TypeCollision_StructVsRecord_BothBackendsAgree()
    {
        // A value-type `s-v` and a record `SV` both sanitize to `SV` (covers the IL
        // struct-definition path, which emits raw names for non-colliding types).
        var source =
            @"(module test)
(define-struct s-v [a : Int])
(define-record SV [b : Int])
(define (compute) : Int (- (SV/b (SV 10)) (s-v/a (s-v 7))))";
        Assert.Equal(3, CompileIlAndRunInt(source));
        Assert.Equal(3, CompileCSharpAndRunInt(source));
    }

    [Fact]
    public void TypeCollision_NoCollision_LeavesTypeNamesUnchanged()
    {
        // Sanity: a lone record is emitted under its plain name (no `_type` suffix).
        var source =
            @"(module test)
(define-record Widget [v : Int])
(define (compute) : Int (Widget/v (Widget 3)))";
        var cs = Compile(source);
        Assert.DoesNotContain("_type", cs);
        Assert.Equal(3, CompileIlAndRunInt(source));
        Assert.Equal(3, CompileCSharpAndRunInt(source));
    }
}
