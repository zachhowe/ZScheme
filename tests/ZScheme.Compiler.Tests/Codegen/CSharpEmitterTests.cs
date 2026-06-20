using System.Runtime.CompilerServices;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Ir;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Codegen;

public class CSharpEmitterTests
{
    // Tests whose emitted C# does not yet compile because of a *known* codegen bug (not a
    // missing reference — the Roslyn harness already links the full test-host dependency
    // closure). Each entry is a genuine defect the harness surfaced. When a bug is fixed, delete
    // its entries here and the harness will start guarding those tests automatically (failing
    // loudly if the output still doesn't compile). Currently empty: no known codegen bugs.
    private static readonly HashSet<string> KnownNonCompilingOutput = [];

    private static string Compile(
        string source,
        bool verifyCompiles = true,
        [CallerMemberName] string caller = ""
    )
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                AllowsImplicitModuleName = true,
                SuppressVersionPreamble = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string>
                {
                    ["stdlib"] = GetStdLibPath(),
                    ["zunit"] = GetZUnitPath(),
                },
                ModuleSearchPaths = [GetZUnitPath()],
                ModuleAliases = new Dictionary<string, string> { ["zunit"] = "zunit/zunit" },
            }
        );
        var result = compilation.Compile(source);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));
        var csResult = (CompilationResult.CSharpOutputResult)result;
        if (verifyCompiles && !KnownNonCompilingOutput.Contains(caller))
            RoslynCompileVerifier.AssertCompiles(
                csResult.CsOutput,
                csResult.PrecompiledAssemblyPaths
            );
        return csResult.CsOutput;
    }

    private static CompilationResult CompileResult(string source)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string>
                {
                    ["stdlib"] = GetStdLibPath(),
                    ["zunit"] = GetZUnitPath(),
                },
                ModuleSearchPaths = [GetZUnitPath()],
                ModuleAliases = new Dictionary<string, string> { ["zunit"] = "zunit/zunit" },
            }
        );
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
        var emitter = new CSharpEmitter(
            diag,
            "TestNameSpace",
            "TestClass",
            suppressVersionPreamble: true
        );
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
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int Add(int x, int y)
                {
                    return (x + y);
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitIntMinValue_AsIntMinValueLiteral()
    {
        // Regression: emitting `int.MinValue` as the bare literal `-2147483648`
        // would cause C# to widen it to `long`, so e.g. `Math.Abs(-2147483648)`
        // would resolve to `Math.Abs(long)` instead of `Math.Abs(int)` and
        // diverge from the IL backend (found via fuzzer).
        var cs = Compile("(module test)\n(define (compute) : Int -2147483648)");
        Assert.Contains("return int.MinValue;", cs);
        Assert.DoesNotContain("-2147483648", cs);
    }

    [Fact]
    public void EmitIntMinValue_InMatchPattern_AsIntMinValueLiteral()
    {
        // Same root cause as EmitIntMinValue_AsIntMinValueLiteral, but for
        // pattern literals: a bare `-2147483648` pattern would be typed as
        // `long` and fail to match an `int` scrutinee.
        var cs = Compile(
            "(module test)\n" + "(define (compute [x : Int]) : Int (match x [-2147483648 1] [_ 0]))"
        );
        Assert.Contains("int.MinValue", cs);
    }

    [Fact]
    public void EmitIfExpression()
    {
        var cs = Compile("(module test)\n(define (abs [x : Int]) : Int (if (< x 0) (- 0 x) x))");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int Abs(int x)
                {
                    return ((x < 0) ? (0 - x) : x);
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitRecursiveFunction()
    {
        var source =
            @"(module test)
(define (factorial [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (factorial (- n 1) (* n acc))))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetBinding()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let [y (+ x 1)] (+ y 2)))");
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    // Regression: `(begin e1 e2 ... en)` desugars to a chain of `(let [_ ei] ...)`.
    // Emitting these as `var _ = ei;` inside a statement body collides on the
    // second `_` (C# CS0128, "already defined in this scope"). They must emit
    // as discard assignments (`_ = ei;`) instead.
    [Fact]
    public void EmitBegin_InTailRecursiveLoop_UsesDiscardAssignments()
    {
        var source =
            @"(module test)
(define (go [x : Int]) : Int
  (if (<= x 0)
      (begin 1 2 x)
      (go (- x 1))))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int Go(int x)
                {
                    while (true)
                    {
                        if ((x <= 0))
                        {
                            _ = 1;
                            _ = 2;
                            return x;
                        }
                        else
                        {
                            var __tmp_0 = (x - 1);
                            x = __tmp_0;
                            continue;
                        }
                    }
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitBegin_NestedInIfThenOfTco_DoesNotRedeclareUnderscore()
    {
        // Before the fix, this produced `var _ = 11; var _ = 22;` on consecutive
        // lines inside the while(true) loop, triggering CS0128 at Roslyn.
        var source =
            @"(module test)
(define (loop [n : Int] [acc : Int]) : Int
  (if (= n 0)
      (begin 10 11 22 acc)
      (loop (- n 1) (+ acc 1))))";
        var cs = Compile(source);
        Assert.DoesNotContain("var _ =", cs);
        Assert.Contains("_ = 10;", cs);
        Assert.Contains("_ = 11;", cs);
        Assert.Contains("_ = 22;", cs);
    }

    [Fact]
    public void EmitBegin_ExplicitUnderscoreLet_UsesDiscard()
    {
        // `(let [_ e] body)` is the desugared form of `(begin e body)`.
        // Even when written explicitly it must not emit `var _ =` at statement
        // level inside a TCO loop.
        var source =
            @"(module test)
(define (run [x : Int]) : Int
  (if (<= x 0)
      (let [_ 99] (let [_ 77] x))
      (run (- x 1))))";
        var cs = Compile(source);
        Assert.DoesNotContain("var _ =", cs);
        Assert.Contains("_ = 99;", cs);
        Assert.Contains("_ = 77;", cs);
    }

    // Regression (fuzzer): inside a `(begin ...)` whose intermediate
    // expression has type Unit (e.g. a void-returning CLR call like
    // `put!` on a Concurrent-Dictionary), the desugar produces `(let [_ <call>] ...)`.
    // Emitting that as `_ = VoidCall();` triggers CS8209 — `void` can't be
    // assigned to a discard. The intermediate must be emitted as a bare
    // statement instead.
    [Fact]
    public void EmitBegin_DiscardingVoidReturningCall_EmitsAsStatement()
    {
        var source =
            @"(module test)
(import stdlib/concurrent/dictionary)

(define (compute) : Int
  (let [d (concurrent-dictionary/new)]
    (begin
      (put! d 0 42)
      (length d))))";
        var cs = Compile(source);
        // The call inside Compute discards put!'s Unit return — must not be `_ = ...;`.
        var putLine = cs.Split('\n')
            .Single(l => l.Contains("(d, 0, 42)"))
            .TrimEnd('\r')
            .TrimStart();
        Assert.StartsWith("Stdlib_Concurrent_DictionaryModule.Put_b", putLine);
    }

    // Regression (fuzzer): a `let` in expression position is lowered to an IIFE
    // `((Func<P,R>)((P p) => body))(value)`. The parameter type `P` comes from
    // `TypeToCs(let.Value.Type)`. When the value is a call to a generic
    // collection constructor (e.g. `concurrent-dictionary/new`) whose return
    // type still has free type variables — because no concrete value flows
    // through the dictionary — `TypeToCs` would special-case the named type
    // (`Concurrent-Dictionary`, `List`, `Map`, ...) and recurse straight into
    // `TypeToCs(arg)`, where a free `ZTypeVar` fell through to the `object`
    // fallback. Sibling positions that went through `FormatTypeArgs`
    // (constructor type args, etc.) defaulted free vars to `int` — so the IIFE
    // parameter ended up `ConcurrentDictionary<int, object>` while the inner
    // operations referenced `ConcurrentDictionary<int, int>`, and Roslyn
    // rejected the C# with CS1503. Defaulting free type vars at the entry of
    // `TypeToCs` keeps the special-cased and generic paths in agreement.
    [Fact]
    public void EmitLet_GenericCollectionValueWithFreeTypeVar_DefaultsToInt()
    {
        var source =
            @"(module test)
(import stdlib/concurrent/dictionary)

(define-union (Either ^a ^b) :where ((^a unmanaged) (^b unmanaged))
  (Lft [v : ^a])
  (Rgt [v : ^b]))

(define (compute) : Int
  (match (Lft -1)
    [(Lft _) 0]
    [(Rgt y)
     (let [d (concurrent-dictionary/new)]
       (begin
         (put! d 0 y)
         (length d)))]))";
        var cs = Compile(source);
        Assert.DoesNotContain("ConcurrentDictionary<int, object>", cs);
        Assert.Contains("ConcurrentDictionary<int, int>", cs);
    }

    [Fact]
    public void EmitBooleanExpression()
    {
        var cs = Compile("(module test)\n(define (check [a : Bool] [b : Bool]) : Bool (and a b))");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static bool Check(bool a, bool b)
                {
                    return (a && b);
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitComparison()
    {
        var cs = Compile("(module test)\n(define (gt [a : Int] [b : Int]) : Bool (> a b))");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static bool Gt(int a, int b)
                {
                    return (a > b);
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitNamespace()
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                Namespace = "MyGame.Logic",
                SuppressVersionPreamble = true,
                DisablePrelude = true,
            }
        );
        var result = compilation.Compile("(module test)\n(define (id [x : Int]) : Int x)");
        Assert.True(result.Success);
        var csResult = (CompilationResult.CSharpOutputResult)result;
        AssertOutput(
            """
            #nullable enable

            namespace MyGame.Logic;


            public static class TestModule
            {
                public static int Id(int x)
                {
                    return x;
                }

            }
            """,
            csResult.CsOutput
        );
    }

    [Fact]
    public void EmitMultipleFunctions()
    {
        var source =
            @"(module test)
(define (add [x : Int] [y : Int]) : Int (+ x y))
(define (dbl [x : Int]) : Int (add x x))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitStringReturn()
    {
        var cs = Compile("(module test)\n(define (greet [name : String]) : String name)");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static string Greet(string name)
                {
                    return name;
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetWithClrCallBody()
    {
        var source =
            @"(import-clr
  [writeln System.Console/WriteLine])

(let [x ""hello""]
  (writeln x))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitNestedLetWithClrCallBody()
    {
        var source =
            @"(import-clr
  [writeln System.Console/WriteLine])

(let [x ""hello""]
  (let [y ""world""]
    (writeln y)))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class UnnamedModule
            {
                public static string X = "hello";
                public static string Y = "world";

                static UnnamedModule()
                {
                    System.Console.WriteLine(Y);
                }
            }
            """,
            cs
        );
    }

    [Fact]
    public void NamespaceDirectiveOverridesDefault()
    {
        var cs = Compile(
            "(module test)\n(namespace My.Game.Logic)\n(define (id [x : Int]) : Int x)"
        );
        AssertOutput(
            """
            #nullable enable

            namespace My.Game.Logic;


            public static class TestModule
            {
                public static int Id(int x)
                {
                    return x;
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void NamespaceDirectiveOverridesCompilerOption()
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.CSharp,
                Namespace = "From.Options",
                SuppressVersionPreamble = true,
                DisablePrelude = true,
            }
        );
        var result = compilation.Compile(
            "(module test)\n(namespace From.Source)\n(define (id [x : Int]) : Int x)"
        );
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));
        var csResult = (CompilationResult.CSharpOutputResult)result;
        AssertOutput(
            """
            #nullable enable

            namespace From.Source;


            public static class TestModule
            {
                public static int Id(int x)
                {
                    return x;
                }

            }
            """,
            csResult.CsOutput
        );
    }

    [Fact]
    public void PipelineProducesValidOutput()
    {
        var source =
            @"(module test)
(define (square [x : Int]) : Int (* x x))";
        var compilation = new Compilation(
            new CompilerOptions { SuppressVersionPreamble = true, DisablePrelude = true }
        );
        var result = compilation.Compile(source);
        Assert.True(result.Success);
        var csResult = (CompilationResult.CSharpOutputResult)result;
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int Square(int x)
                {
                    return (x * x);
                }

            }
            """,
            csResult.CsOutput
        );
    }

    [Fact]
    public void ModuleDecl_SetsClassName()
    {
        var cs = Compile("(module core)\n(define (id [x : Int]) : Int x)");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class CoreModule
            {
                public static int Id(int x)
                {
                    return x;
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void ModuleDecl_HierarchicalName()
    {
        var cs = Compile("(module math/vector)\n(define (id [x : Int]) : Int x)");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class Math_VectorModule
            {
                public static int Id(int x)
                {
                    return x;
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void ModuleDecl_HyphenatedName()
    {
        var cs = Compile("(module my-utils)\n(define (id [x : Int]) : Int x)");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class MyUtilsModule
            {
                public static int Id(int x)
                {
                    return x;
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void NoModuleDecl_WithDefine_ReportsError()
    {
        var compilation = new Compilation(
            new CompilerOptions { OutputMode = OutputMode.CSharp, DisablePrelude = true }
        );
        var result = compilation.Compile("(define (id [x : Int]) : Int x)");
        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("require a (module ...) declaration")
        );
    }

    [Fact]
    public void EmitObjectExpr_SingleInterface()
    {
        var source =
            @"(module test)
(define-interface IComparer
  (Compare [x : Int] [y : Int] : Int))
(define (make-comparer) : IComparer
  (object IComparer
    (define (Compare [x : Int] [y : Int]) : Int
      (- x y))))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public interface IComparer
                {
                    int Compare(int x, int y);
                }

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
            """,
            cs
        );
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
        var source =
            @"(module test)
(define-interface IA (get-a : Int))
(define-interface IB (get-b : Int))
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
        var source =
            @"(module test)
(define-interface IFoo
  (DoFoo [] : Int))
(define-interface IBar
  (DoBar [x : Int] : Int))
(define (make-obj) : IFoo
  (object (IFoo IBar)
    (define (DoFoo) : Int 42)
    (define (DoBar [x : Int]) : Int x)))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public interface IFoo
                {
                    int DoFoo();
                }

                public interface IBar
                {
                    int DoBar(int x);
                }

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
            """,
            cs
        );
    }

    [Fact]
    public void EmitObjectExpr_WithFieldlessBaseClass()
    {
        // An object expression extending a base class that has no fields (so its
        // auto-generated constructor is parameterless) needs no explicit
        // (constructor (super ...)): the emitted `: base()` resolves against
        // the parameterless base ctor. This exercises the no-arg base() path in
        // EmitObjectClasses; the with-super-args path is covered by
        // EmitObjectExpr_WithBaseClassAndConstructor.
        var source =
            @"(module test)
(define-class #:open Animal
  (define (Speak) : String ""generic""))

(define (make-cat) : Animal
  (object : Animal
    (define (Speak) : String ""meow"")))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public class Animal
                {

                    public Animal()
                    {
                    }

                    public virtual string Speak()
                    {
                        return "generic";
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitObjectExpr_WithBaseClassAndInterface()
    {
        var source =
            @"(module test)
(define-interface ISerializable
  (Serialize [] : String))

(define-class #:open Animal
  [name : String]
  (define (Speak) : String name))

(define (make-cat) : Animal
  (object : Animal ISerializable
    (constructor (super ""Cat""))
    (define (Speak) : String ""meow"")
    (define (Serialize) : String ""cat"")))";
        var cs = Compile(source);
        AssertOutput(
            """
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
                    public __Object_0() : base("Cat")
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitObjectExpr_WithBaseClassAndConstructor()
    {
        var source =
            @"(module test)
(define-class #:open Animal
  [name : String]
  [sound : String]
  (define (Speak) : String name))

(define (make-cat) : Animal
  (object : Animal
    (constructor (super ""Cat"" ""meow""))
    (define (Speak) : String ""I am a cat"")))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitObjectExpr_SuperArgsReferencingOuterParamAreCaptured()
    {
        // Regression: before the fix, super args in an object expression were
        // emitted verbatim into the generated nested class, referencing names
        // that are out of scope (the enclosing method's parameters). The fix
        // treats super args as part of the capture analysis and passes them
        // through the constructor, then uses the ctor parameter inside base().
        var source =
            @"(module test)
(define-class #:open Animal
  [name : String]
  [sound : String]
  (define (Speak) : String name))

(define (make-animal [n : String]) : Animal
  (object : Animal
    (constructor (super n ""unknown""))
    (define (Speak) : String ""hello"")))";
        var cs = Compile(source);
        // The outer call-site passes the outer 'n' through to the nested ctor.
        Assert.Contains("new __Object_0(n)", cs);
        // The nested ctor takes a captured parameter typed from the outer's
        // ZType (string), not erased to object, so base(string, string)
        // resolves without a CS1503 implicit-conversion error.
        Assert.Contains("__Object_0(string n_param) : base(n_param, \"unknown\")", cs);
        // The capture is stored as a field (existing behavior, validated here
        // to guard against regressions in the new save/restore of the
        // captured-fields map around the constructor).
        Assert.Contains("this.N_field = n_param;", cs);
    }

    [Fact]
    public void EmitObjectExpr_NestedInsideOuterObjectConstructorAppliesOuterRename()
    {
        // Regression (found by fuzzer seed 0x31a453b8): when an object
        // expression is nested inside the super-args of an *outer* object
        // expression, the outer object's ctor renames the captured outer
        // variable `p0` to `p0_param`. The inner object's call site
        // (`new __Object_1(p0)`) is emitted inside that ctor, so `p0` must
        // resolve through the outer ctor's rename map too. Previously the
        // nested call emitted the bare sanitized name and Roslyn rejected
        // the output with CS0103: "The name 'p0' does not exist in the
        // current context." The fix routes each capture argument through
        // EmitVar so the outer scope's rewrites apply.
        var source =
            @"(module test)
(define-class #:open FCls_0
  [f0 : Int])

(define (top [p0 : Int]) : Int
  (let [outer (object : FCls_0
    (constructor (super (begin
      (object : FCls_0 (constructor (super p0)))
      p0))))]
    p0))";
        var cs = Compile(source);
        // The outer call-site still passes outer-scope 'p0' directly (no
        // rewrite needed — it is a plain function parameter there).
        Assert.Contains("new __Object_0(p0)", cs);
        // Inside __Object_0's ctor, 'p0' is renamed to 'p0_param'. The
        // nested `new __Object_1(...)` must use the renamed identifier.
        Assert.Contains("new __Object_1(p0_param)", cs);
        Assert.DoesNotContain("new __Object_1(p0)", cs);
    }

    [Fact]
    public void EmitObjectExpr_NestedInsideClassMethodCapturesClassField()
    {
        // Sibling case: when an object expression appears inside a class
        // method body and captures a class field, the emitted call site
        // must resolve the field to `this.<Field>`, not emit the raw field
        // name (which would fail to resolve at class scope or could shadow
        // with a local of the same name). This also verifies the fix does
        // not regress: EmitVar's class-field branch must be reachable when
        // emitting the capture arg list.
        var source =
            @"(module test)
(define-interface IBox
  (get : Int))

(define-class Holder
  [value : Int]
  (define (make) : IBox
    (object IBox
      (define (get) : Int value))))";
        var cs = Compile(source);
        // The call site runs inside Holder.Make, where `value` resolves to
        // `this.Value`. That must show up in the ctor argument list too.
        Assert.Contains("new __Object_0(this.Value)", cs);
        Assert.DoesNotContain("new __Object_0(value)", cs);
    }

    [Fact]
    public void EmitObjectExpr_NestedInsideClassMethodCapturesClassFieldShadowingTopLevelFunction()
    {
        // Regression: a fuzzer case (seeds 0x69a681ca, 0x802f9650, etc.) exposed
        // a defect where an object expression nested inside a class method body
        // referenced a class field whose name also matched a module-level
        // function. CollectCapturedVars's module-name skip fired before the
        // class-field check, so the field was never captured; the inner object
        // class then emitted the bare sanitized name (`F0`), which Roslyn
        // resolved to the static module function and rejected with CS0428
        // ("Cannot convert method group 'F0' to non-delegate type 'int'").
        // Class fields take precedence in EmitVar — capture analysis must
        // mirror that precedence.
        var source =
            @"(module test)
(define-interface IBox
  (get : Int))

(define-class Holder
  [f0 : Int]
  (define (make) : IBox
    (object IBox
      (define (get) : Int f0))))

(define (f0 [x : Int]) : Int (* x 2))";
        var cs = Compile(source);
        // The capture is threaded through the ctor arg list as `this.F0`
        // (the field, not the static function), and the inner class reads
        // it back through the captured backing field.
        Assert.Contains("new __Object_0(this.F0)", cs);
        Assert.Contains("private readonly int F0_field;", cs);
        // The inner Get() body must NOT emit a bare `F0` — that would
        // resolve to the module-level function and bring back CS0428.
        Assert.Contains("return this.F0_field;", cs);
        Assert.DoesNotContain("return F0;", cs);
    }

    [Fact]
    public void EmitCall_FunctionTypedParameterShadowsTopLevelGenericFunction()
    {
        // Regression: a fuzzer case (master seed 0xa0ed99af, case 0x2c112f16 and
        // 9 sibling cases) exposed a defect where a call to a function-typed
        // parameter emitted explicit method type arguments because a top-level
        // generic function shared the parameter's name.
        //
        // `apply-fn` has a delegate parameter `f1`; a separate module-level
        // generic function is also named `f1`. EmitVarRef already resolves the
        // unqualified `f1` to the parameter (locals shadow module functions),
        // but EmitCall's generic-instantiation path consulted _genericFuncs by
        // bare name only and matched the top-level `f1`, emitting
        // `f1<T0, int>(x)`. Roslyn rejects type arguments on a delegate
        // invocation with CS0307 ("cannot be used with type arguments").
        // Fix: EmitCall mirrors EmitVarRef's shadowing precedence and skips the
        // generic path for an unqualified call whose name is a local binding.
        var source =
            @"(module test)
(define (apply-fn [f1 : (^a -> ^b)] [x : ^a]) : ^b (f1 x))
(define (f1 [a : ^a] [b : ^b]) : ^a a)
(define (main) : Int (apply-fn (lambda ([n : Int]) (+ n 1)) 5))";
        var cs = Compile(source);
        // The call to the delegate parameter must be a plain invocation; the
        // type arguments belong only on the call to the top-level `f1`.
        Assert.Contains("return f1(x);", cs);
        Assert.DoesNotContain("f1<", cs);
    }

    [Fact]
    public void EmitCall_StdlibComposeParamShadowsUserGenericFunctionWithSameName()
    {
        // The fuzzer reproduction in its original cross-module form: stdlib's
        // `compose` (parameters `f1`/`f2`) is emitted inline alongside a user
        // module that defines a top-level generic function named `f1`. The
        // global _genericFuncs table is keyed by bare name across all emitted
        // modules, so without the local-binding guard, compose's body emitted
        // `f2<T1, int>(f1<T0, int>(x))` and Roslyn rejected it (CS0307). The
        // Compile helper's Roslyn verifier would also fail outright.
        var source =
            @"(module test)
(import stdlib/core)
(define (f1 [a : ^a] [b : ^b]) : ^a a)
(define (main) : Int
  ((compose (lambda ([x : Int]) (+ x 1)) (lambda ([y : Int]) (* y 2))) 10))";
        var cs = Compile(source);
        // Compose's body invokes its delegate parameters directly.
        Assert.Contains("return ((T0 x) => f2(f1(x)));", cs);
        Assert.DoesNotContain("f1<", cs);
        Assert.DoesNotContain("f2<", cs);
    }

    [Fact]
    public void EmitObjectExpr_NestedObjectInsideObjectMethodCapturesClassFieldShadowingTopLevelFunction()
    {
        // Regression: a fuzzer case (seed 0x4157f2ba/case 0xb3f20563) exposed
        // a defect where TWO levels of object-expression nesting inside a class
        // method dropped the inner object's capture of a class field whose name
        // matched a module-level function.
        //
        // CollectCapturedVars consulted _currentClassFields to detect that a
        // free var should be captured (not skipped as a module function), but
        // object-class bodies are emitted by EmitObjectClasses *after*
        // EmitClassDecl returns — at which point _currentClassFields has been
        // cleared. When an inner object expression appeared in an outer
        // object's method body, the inner's free-var analysis ran with
        // _currentClassFields == null and skipped `f0` as a module function.
        // The inner anonymous class then emitted `return F0;` which Roslyn
        // resolved to the static module function (CS0428).
        //
        // Fix: capture analysis also consults _currentObjectCapturedFields,
        // which is populated to the enclosing object's captures during nested
        // emission. A name that the outer object captured can be re-captured
        // by the inner object regardless of whether a module-level function
        // shares the name.
        var source =
            @"(module test)
(define-interface IBox
  (get : Int))

(define-class Holder
  [f0 : Int]
  (define (make) : IBox
    (object IBox
      (define (get) : Int
        (let [inner : IBox (object IBox
                             (define (get) : Int f0))]
          f0)))))

(define (f0 [x : Int]) : Int (* x 2))";
        var cs = Compile(source);
        // Both the outer and inner anonymous classes carry F0 as a captured
        // field, and the outer threads its captured value into the inner ctor
        // rather than the bare `F0` (which would bind to the static method).
        Assert.Contains("new __Object_0(this.F0)", cs);
        Assert.Contains("new __Object_1(this.F0_field)", cs);
        Assert.DoesNotContain("new __Object_1(F0)", cs);
        Assert.DoesNotContain("return F0;", cs);
    }

    [Fact]
    public void EmitObjectExpr_ModuleFunctionInBodyIsNotCaptured()
    {
        // Regression: a fuzzer case surfaced two defects in the object-expression
        // capture analysis when a method body invoked a module-scope function.
        //   1. The emitter passed the module function's unqualified name as a
        //      ctor argument at the call site (`new __Object_0(helper, v)`),
        //      but `helper` does not exist as a local there — Roslyn rejected
        //      the output with CS0103.
        //   2. Captured variables were erased to `object`, so calling
        //      `this.Helper_field(this.V_field)` failed with CS1955 and the
        //      `Helper(this.V_field)` resolution also tripped CS1503.
        // Module-scope names resolve via EmitVar's qualified-member lookup,
        // so they must be excluded from capture analysis. Remaining captures
        // keep their ZType instead of being boxed to `object`.
        var source =
            @"(module test)
(define-interface IBox
  (get : Int))

(define (helper [x : Int]) : Int (+ x 1))

(define (make-box [v : Int]) : IBox
  (object IBox
    (define (get) : Int (helper v))))";
        var cs = Compile(source);
        // The module function 'helper' is not in the ctor-arg list.
        Assert.Contains("new __Object_0(v)", cs);
        Assert.DoesNotContain("new __Object_0(helper", cs);
        // The captured local 'v' keeps its Int type instead of being `object`.
        Assert.Contains("private readonly int V_field;", cs);
        Assert.Contains("public __Object_0(int v_param)", cs);
        // The method body calls the module function directly and passes the
        // typed capture without unboxing.
        Assert.Contains("return Helper(this.V_field);", cs);
    }

    [Fact]
    public void EmitObjectExpr_ModuleValueBindingInBodyIsNotCaptured()
    {
        // Module-level `let` bindings (non-function values) live on the module
        // class as static members and must be excluded from capture analysis
        // the same way module functions are — EmitVar resolves them through
        // _currentModuleNames. Previously they were captured as `object`,
        // producing the same CS0103 / CS1503 pair.
        var source =
            @"(module test)
(define-interface IBox
  (get : Int))

(define base-value 100)

(define (make-box [v : Int]) : IBox
  (object IBox
    (define (get) : Int (+ base-value v))))";
        var cs = Compile(source);
        Assert.Contains("new __Object_0(v)", cs);
        Assert.DoesNotContain("new __Object_0(base", cs);
        Assert.Contains("private readonly int V_field;", cs);
    }

    [Fact]
    public void EmitObjectExpr_ImportedModuleFunctionInBodyIsNotCaptured()
    {
        // Same defect as EmitObjectExpr_ModuleFunctionInBodyIsNotCaptured, but
        // the function lives in an imported module (hits the _funcToModuleClass
        // arm of EmitVar instead of _currentModuleNames). Both arms emit a
        // qualified call site, so neither should appear as a capture.
        var source =
            @"(module test)
(import stdlib/option)
(define-interface IBox
  (get : (Option Int)))

(define (make-box [v : Int]) : IBox
  (object IBox
    (define (get) : (Option Int) (Some v))))";
        var cs = Compile(source);
        // 'Some' is a union case constructor (not a Var), so it stays out of
        // the capture list through a separate path — assert here to cover it
        // along with the ctor argument list containing only the local capture.
        Assert.Contains("new __Object_0(v)", cs);
        Assert.Contains("private readonly int V_field;", cs);
    }

    [Fact]
    public void EmitRecord_AppearsAfterPreambleNoProgramClass()
    {
        var cs = Compile("(define-record Point [x : Float] [y : Float])");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;

            public sealed record Point(float X, float Y);

            """,
            cs
        );
    }

    [Fact]
    public void EmitStruct_EmitsRecordStruct()
    {
        var cs = Compile("(define-struct Point [x : Int] [y : Int])");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;

            public readonly record struct Point(int X, int Y);

            """,
            cs
        );
    }

    [Fact]
    public void EmitStruct_Generic_EmitsRecordStructWithTypeParams()
    {
        var cs = Compile("(define-struct (Box a) [value : a])");
        Assert.Contains("public readonly record struct Box<T0>(T0 Value);", cs);
    }

    [Fact]
    public void EmitStruct_New_EmitsConstructorCall()
    {
        var source =
            @"(module test)
(define-struct Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        Assert.Contains("public readonly record struct Point(int X, int Y);", cs);
        Assert.Contains("new Point(X: 0, Y: 0)", cs);
    }

    [Fact]
    public void EmitStruct_With_EmitsRecordStructWith()
    {
        var source =
            @"(module test)
(define-struct Point [x : Int] [y : Int])
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
        var source =
            @"(module test)
(define-struct Point [x : Int] [y : Int])
(define (mk) : Point (new Point 1 2))";
        var cs = Compile(source);
        Assert.Contains("new Point(X: 1, Y: 2)", cs);
    }

    [Fact]
    public void EmitUnion_AppearsAfterPreambleNoProgramClass()
    {
        var cs = Compile(
            "(define-union Shape (Circle [r : Float]) (Rect [w : Float] [h : Float]))"
        );
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;

            public abstract record Shape;
            public sealed record Circle(float R) : Shape;
            public sealed record Rect(float W, float H) : Shape;


            """,
            cs
        );
    }

    [Fact]
    public void EmitRecord_PreambleComesFirst()
    {
        var cs = Compile("(define-record Point [x : Int])");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;

            public sealed record Point(int X);

            """,
            cs
        );
    }

    [Fact]
    public void EmitRecordAndFunction_CorrectOrdering()
    {
        var source =
            @"(module test)
(define-record Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDeclOnly_NoProgramClass()
    {
        var source =
            @"(define-class Point
  [x : Int]
  [y : Int]
  (define (magnitude) : Int
    (+ (* x x) (* y y))))";
        var cs = Compile(source);
        AssertOutput(
            """
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

            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDecl_OpenClass_NotSealed()
    {
        var source =
            @"(define-class #:open Animal
  [name : String]
  (define (Speak) : String name))";
        var cs = Compile(source);
        AssertOutput(
            """
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

            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDecl_Inheritance_BaseClassInList()
    {
        var source =
            @"(define-class #:open Animal
  [name : String])

(define-class Dog : Animal
  [breed : String])";
        var cs = Compile(source);
        AssertOutput(
            """
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

            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDecl_Inheritance_OverrideMethod()
    {
        var source =
            @"(define-class #:open Animal
  [name : String]
  (define (Speak) : String name))

(define-class Dog : Animal
  [breed : String]
  (define (Speak) : String breed))";
        var cs = Compile(source);
        AssertOutput(
            """
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

            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDecl_Inheritance_SuperMethodCall()
    {
        var source =
            @"(define-class #:open Animal
  [name : String]
  (define (Speak) : String name))

(define-class Dog : Animal
  (define (Speak) : String
    (string-append (super/Speak) ""!"")))";
        var cs = Compile(source);
        AssertOutput(
            """
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

            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDecl_Inheritance_BaseClassAndInterface()
    {
        var source =
            @"(define-interface IService
  (GetName [] : String))

(define-class #:open Base
  [name : String]
  (define (GetName) : String name))

(define-class Impl : Base IService)";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;

            public interface IService
            {
                string GetName();
            }

            public class Base
            {
                public string Name { get; }

                public Base(string Name)
                {
                    this.Name = Name;
                }

                public virtual string GetName()
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

            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDecl_ExplicitConstructor_WithSuper()
    {
        var source =
            @"(define-class #:open Animal
  [name : String]
  (define (Speak) : String name))

(define-class Dog : Animal
  [breed : String]
  (constructor [nickname : String]
    (super nickname)
    (set! breed ""mixed"")))";
        var cs = Compile(source);
        AssertOutput(
            """
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

            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDecl_ExplicitConstructor_NoBase()
    {
        var source =
            @"(define-class Widget
  [name : String]
  [size : Int]
  (constructor [n : String]
    (set! name n)
    (set! size 0)))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDecl_SetFieldInBegin_EmitsBareAssignmentStatement()
    {
        // Regression (fuzzer seed 0x3f6f6490): `(begin (set! f x) f)` inside a
        // method body desugars to `Let _ = SetField in FieldGet`. The emitter
        // wrapped SetField as `(this.F = X)` which is a valid C# expression but
        // not a valid statement — Roslyn rejected `(this.F = X);` with CS0201
        // ("only assignment, call, increment, decrement, await and new object
        // expressions can be used as a statement"). The fix removes the outer
        // parens so SetField emits a bare `this.F = X` that works in both
        // statement and expression positions.
        var source = """
            (module test)
            (define-class Box
              [v : Int #:mutable]
              (constructor [start : Int] (set! v start))
              (define (Bump) : Int (begin (set! v 5) v)))
            """;
        var cs = Compile(source);
        // The bug-shape we are guarding against: a parenthesized assignment
        // immediately followed by a semicolon (statement position).
        Assert.DoesNotContain("(this.V = 5);", cs);
        // And we should see the bare assignment as a statement.
        Assert.Contains("this.V = 5;", cs);
    }

    [Fact]
    public void EmitClassDecl_LetInMethodBody_BindingIsLocalNotStatic()
    {
        // Regression: A `let` binding inside a class method body was being emitted
        // as `ClassName.Hello` (a static-member access) instead of the local
        // variable `hello` introduced by the surrounding lambda.
        var source = """
            (module test)
            (define-class Box
              [v : Int]
              (define (Bump) : Int
                (let [hello 5] (+ hello 1))))
            """;
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public sealed class Box
                {
                    public int V { get; }

                    public Box(int V)
                    {
                        this.V = V;
                    }

                    public int Bump()
                    {
                        return ((System.Func<int, int>)((int hello) => (hello + 1)))(5);
                    }
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDecl_MatchInMethodBody_PatternVariableIsLocalNotStatic()
    {
        // Regression: A `match` pattern variable referenced in the arm body was
        // being emitted as `ClassName.X4` instead of the local `x4` bound by the
        // surrounding switch arm.
        var source = """
            (module test)
            (define-class Box
              [v : Int]
              (define (Pick) : Int
                (match 5 [x4 (+ x4 1)])))
            """;
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public sealed class Box
                {
                    public int V { get; }

                    public Box(int V)
                    {
                        this.V = V;
                    }

                    public int Pick()
                    {
                        return 5 switch { var x4 => (x4 + 1), };
                    }
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitClassDecl_NestedMatchInMethodBody_OuterPatternVariableInScope()
    {
        // Regression: A reference to an outer `match` pattern variable from
        // inside a nested `match` (both inside a class method) was being
        // emitted as `ClassName.X4` rather than the local `x4`.
        var source = """
            (module test)
            (define-union (Box ^a) (Wrap [v : ^a]) (Empty))
            (define-class Holder
              [f0 : Int]
              (define (Run [p0 : Int]) : Int
                (match 5
                  [x4
                   (match (Wrap x4)
                     [(Wrap _) x4]
                     [Empty x4])])))
            """;
        var cs = Compile(source);
        // Verify the outer pattern variable reference inside the nested match
        // arm is the local `x4`, not the static-class accessor `TestModule.X4`.
        Assert.DoesNotContain("TestModule.X4", cs);
        Assert.Contains("var x4 =>", cs);
        Assert.Contains("(_) => x4", cs);
    }

    [Fact]
    public void EmitClassDecl_MethodCallsTopLevelFunction_QualifiedWithModule()
    {
        // The fix must still qualify top-level function calls with the module
        // class when emitted from inside a nested class method.
        var source = """
            (module test)
            (define (helper [x : Int]) : Int (+ x 1))
            (define-class Box
              [v : Int]
              (define (Compute) : Int (helper v)))
            """;
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int Helper(int x)
                {
                    return (x + 1);
                }

                public sealed class Box
                {
                    public int V { get; }

                    public Box(int V)
                    {
                        this.V = V;
                    }

                    public int Compute()
                    {
                        return TestModule.Helper(this.V);
                    }
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitMatch_WildcardArm_NoFallback()
    {
        var source =
            @"(module test)
(define-union Color (Red) (Green) (Blue))
(define (name [c : Color]) : Int
  (match c
    [(Red) 1]
    [(Green) 2]
    [_ 3]))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitMatch_VariableArm_NoFallback()
    {
        var source =
            @"(module test)
(define (describe [x : Int]) : Int
  (match x
    [0 0]
    [other other]))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int Describe(int x)
                {
                    return x switch { 0 => 0, var other => other, };
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitMatch_RecordStructConstructorPattern_NoFallbackArm()
    {
        // Regression (fuzzer seed 0x845dd508): a `match` whose only arm is a
        // constructor pattern over a single-case record/struct (`(SRec_1 x y z)`
        // with all-variable subpatterns) is exhaustive in C# — Roslyn knows the
        // scrutinee can only be that one record shape. Emitting a trailing
        // `_ => throw ...` fallback tripped CS8510 ("pattern is unreachable")
        // and broke compilation of the generated C#.
        var source =
            @"(module test)
(define-struct SRec [a : Int] [b : Int] [c : Int])
(define (compute) : Int
  (match (SRec 1 2 3)
    [(SRec x y z) (+ x (+ y z))]))";
        var cs = Compile(source);
        Assert.Contains("SRec(var x, var y, var z) => ", cs);
        Assert.DoesNotContain("Non-exhaustive match", cs);
    }

    [Fact]
    public void EmitMatch_RecordClassConstructorPattern_NoFallbackArm()
    {
        // Same root cause as the record-struct regression, but for `record`
        // (single-case sealed record class). All-variable destructuring is
        // exhaustive, so no fallback arm should be emitted.
        var source =
            @"(module test)
(define-record Wrap [v : Int])
(define (compute) : Int
  (match (Wrap 7) [(Wrap v) v]))";
        var cs = Compile(source);
        Assert.Contains("Wrap(var v) => v", cs);
        Assert.DoesNotContain("Non-exhaustive match", cs);
    }

    [Fact]
    public void EmitMatch_UnionConstructorPattern_KeepsFallbackArm()
    {
        // Inverse of the record-pattern fix: a constructor pattern over one
        // *case* of a multi-case union is still refutable (sibling cases remain
        // unmatched), so the trailing `_ =>` fallback is required.
        var source =
            @"(module test)
(define-union U (A [v : Int]) (B [v : Int]))
(define (compute [u : U]) : Int
  (match u [(A x) x]))";
        var cs = Compile(source);
        Assert.Contains("Non-exhaustive match", cs);
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
        var source =
            @"(module test)
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
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class UnnamedModule
            {
                public static System.Object Obj = new System.Object();
            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitClrNew_WithArgs()
    {
        var cs = Compile("(let [lst (new System.Collections.ArrayList 10)] lst)");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class UnnamedModule
            {
                public static System.Collections.ArrayList Lst = new System.Collections.ArrayList(10);
            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetInFuncBody_EmitsVarDeclaration()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let [y (+ x 1)] (+ y 2)))");
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetStarInFuncBody_EmitsVarDeclarations()
    {
        var cs = Compile(
            "(module test)\n(define (f [a : Int] [b : Int]) : Int (let* ([x (* a 2)] [y (+ x b)]) (+ x y)))"
        );
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetWithShadowing_StillUsesIIFE()
    {
        var cs = Compile(
            "(module test)\n(define (f [x : Int]) : Int (let* ([x (+ x 1)] [x (* x 2)]) x))"
        );
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int F(int x)
                {
                    return ((System.Func<int, int>)((int x) => ((System.Func<int, int>)((int x) => x))((x * 2))))((x + 1));
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetWithTypeAnnotation_InFuncBody()
    {
        var cs = Compile(
            "(module test)\n(define (f [x : Int]) : Int (let [y : Int (+ x 1)] (+ y 2)))"
        );
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetWithNullableAnnotation_InFuncBody()
    {
        var cs = Compile("(module test)\n(define (f [x : Int]) : Int (let [y : Int? (+ x 1)] 42))");
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetStarWithTypeAnnotations_InFuncBody()
    {
        var cs = Compile(
            "(module test)\n(define (f [x : Int]) : Int (let* ([a : Int (+ x 1)] [b : Int (* a 2)]) (+ a b)))"
        );
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetWithTypeAnnotation_TopLevel()
    {
        var source =
            @"(module test)
(import-clr
  [writeln System.Console/WriteLine])
(let [s : System.IO.Stream (new System.IO.MemoryStream)]
  (writeln ""created stream""))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetWithTypeAnnotation_ShadowingUsesIIFE()
    {
        var cs = Compile(
            "(module test)\n(define (f [x : Int]) : Int (let* ([x : Int (+ x 1)] [x : Int (* x 2)]) x))"
        );
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int F(int x)
                {
                    return ((System.Func<int, int>)((int x) => ((System.Func<int, int>)((int x) => x))((x * 2))))((x + 1));
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitLetWithTypeAnnotation_TcoBody()
    {
        var source =
            @"(module test)
(define (f [n : Int] [acc : Int]) : Int
  (if (= n 0) acc (let [m : Int (- n 1)] (f m (* n acc)))))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitTestCase_SingleAssertion()
    {
        var source =
            @"(module test)
(import zunit)
(import-clr
  [check-true Xunit.Assert/True])

(test-case booleans-work
  (check-true #t))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitTestCase_MultipleAssertions()
    {
        var source =
            @"(module test)
(import zunit)
(import-clr
  [check-equal Xunit.Assert/Equal ^a]
  [check-true  Xunit.Assert/True])

(test-case multiple-checks
  (check-equal 1 1)
  (check-true #t))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            using Xunit;

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                [Xunit.FactAttribute]
                public static void MultipleChecks()
                {
                    ((System.Func<System.ValueTuple>)(() => { Xunit.Assert.Equal<int>(1, 1); Xunit.Assert.True(true); return default(System.ValueTuple); }))();
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitTestCase_WithExpression()
    {
        var source =
            @"(module test)
(import zunit)
(import-clr
  [check-equal Xunit.Assert/Equal ^a])

(test-case addition-works
  (check-equal (+ 1 2) 3))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            using Xunit;

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                [Xunit.FactAttribute]
                public static void AdditionWorks()
                {
                    Xunit.Assert.Equal<int>(unchecked(1 + 2), 3);
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitTestCase_CoexistsWithDefine()
    {
        var source =
            @"(module test)
(import zunit)
(import-clr
  [check-equal Xunit.Assert/Equal ^a])

(define (add [x : Int] [y : Int]) : Int (+ x y))

(test-case add-works
  (check-equal (add 1 2) 3))";
        var cs = Compile(source);
        AssertOutput(
            """
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
                    Xunit.Assert.Equal<int>(Add(1, 2), 3);
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitRaiseExpression()
    {
        var source =
            @"(module test)
(define (fail) : Int
  (raise (new System.Exception ""boom"")))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int Fail()
                {
                    throw new System.Exception("boom");
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitRaiseInIfBranch()
    {
        var source =
            @"(module test)
(define (check [x : Int]) : Int
  (if (> x 0) x (raise (new System.ArgumentException ""negative""))))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int Check(int x)
                {
                    return ((x > 0) ? x : throw new System.ArgumentException("negative"));
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitRaiseInFunctionBody()
    {
        var source =
            @"(module test)
(define (not-implemented) : Int
  (raise (new System.NotImplementedException ""todo"")))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int NotImplemented()
                {
                    throw new System.NotImplementedException("todo");
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitAsyncFunction()
    {
        var cs = Compile("(module test)\n(define-async (compute [x : Int]) : (Task Int) (+ x 1))");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static async System.Threading.Tasks.Task<int> Compute(int x)
                {
                    return (x + 1);
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitAwaitExpression()
    {
        var source =
            @"(module test)
(define-async (compute [x : Int]) : (Task Int) (+ x 1))
(define-async (use-it [x : Int]) : (Task Int) (await (compute x)))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitAwaitInLet_EmitsVarStatement_NotLambda()
    {
        var source =
            @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int)
  (let [result (await (inner x))]
    (+ result 10)))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitAwait_NoWrappingParens()
    {
        var source =
            @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task Int) (await (inner x)))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitAsyncNonGenericTask_NoReturnStatement()
    {
        var source =
            @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (do-work) : Task (await (inner 42)))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitNestedLetWithAwait_EmitsSequentialStatements()
    {
        var source =
            @"(module test)
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (chain [x : Int]) : (Task Int)
  (let [a (await (step x))]
    (let [b (await (step a))]
      (+ a b))))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitAwaitInIfBranches()
    {
        var source =
            @"(module test)
(define-async (step [x : Int]) : (Task Int) (+ x 1))
(define-async (choose [flag : Bool] [x : Int]) : (Task Int)
  (if flag (await (step x)) (await (step 0))))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitAsyncWithoutAwait_EmitsReturn()
    {
        var cs = Compile("(module test)\n(define-async (simple [x : Int]) : (Task Int) (+ x 1))");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static async System.Threading.Tasks.Task<int> Simple(int x)
                {
                    return (x + 1);
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitAwaitNonGenericTaskInLet()
    {
        var source =
            @"(module test)
(define-async (side-effect) : Task 0)
(define-async (use-it) : (Task Int)
  (let [_ (await (side-effect))]
    42))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static async System.Threading.Tasks.Task SideEffect()
                {
                    _ = 0;
                }

                public static async System.Threading.Tasks.Task<int> UseIt()
                {
                    await SideEffect();
                    return 42;
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitGenericIdentityFunction()
    {
        var cs = Compile("(module test)\n(define (id [x : ^a]) : ^a x)");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static T0 Id<T0>(T0 x)
                {
                    return x;
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitGenericMultiTypeParams()
    {
        var cs = Compile("(module test)\n(define (const [x : ^a] [y : ^b]) : ^a x)");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static T0 Const<T0, T1>(T0 x, T1 y)
                {
                    return x;
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitGenericHigherOrderFunction()
    {
        var cs = Compile("(module test)\n(define (apply [f : (^a -> ^b)] [x : ^a]) : ^b (f x))");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static T1 Apply<T0, T1>(System.Func<T0, T1> f, T0 x)
                {
                    return f(x);
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitGenericWithCollectionType()
    {
        var cs = Compile(
            "(module test)\n(import stdlib/treelist)\n(define (wrap [x : ^a]) : (TreeList ^a) (treelist x))"
        );
        // Verify the key shape: a wrap function that takes T0 and returns ImmutableList<T0>,
        // delegating to stdlib's treelist constructor. The detailed snapshot of the rest of the
        // stdlib emit is brittle against unrelated stdlib changes.
        Assert.Contains(
            "public static System.Collections.Immutable.ImmutableList<T0> Wrap<T0>(T0 x)",
            cs
        );
        Assert.Contains("Stdlib_TreelistModule.Treelist<T0>(", cs);
        Assert.Contains("public static class Stdlib_TreelistModule", cs);
    }

    [Fact]
    public void EmitMonomorphicFunctionHasNoTypeParams()
    {
        var cs = Compile("(module test)\n(define (add [x : Int] [y : Int]) : Int (+ x y))");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int Add(int x, int y)
                {
                    return (x + y);
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitExpr_UnhandledNodeType_ReportsError()
    {
        var typeTest = new IrNode.TypeTest(
            new IrNode.Var("x") { Type = ZType.Int },
            "SomeType",
            "bound"
        )
        {
            Type = ZType.Bool,
        };
        var funcDef = new IrNode.FuncDef(
            "test_func",
            [new IrParam("x", ZType.Int)],
            ZType.Bool,
            typeTest,
            false
        );
        var seq = new IrNode.Seq([funcDef]);
        var (_, diag) = EmitDirect(seq);
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.IsError && d.Message.Contains("C# emission not implemented for")
        );
    }

    [Fact]
    public void EmitExpr_HandledNodeType_NoError()
    {
        var result = CompileResult(
            "(module test)\n(define (add [x : Int] [y : Int]) : Int (+ x y))"
        );
        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("C# emission not implemented")
        );
    }

    [Fact]
    public void TypeToCs_UnresolvedTypeVar_DefaultsToInt()
    {
        // A free `ZTypeVar` (one not bound by an enclosing generic function's
        // type-param map) is defaulted to `int` at the C# emission boundary.
        // `int` satisfies every constraint we emit (`unmanaged`, `struct`,
        // `notnull`); `object` does not, and emitting `object` would also
        // disagree with sibling positions that go through `FormatTypeArgs` and
        // already pick `int` — producing C# Roslyn rejects.
        var unresolvedVar = new IrNode.Var("x") { Type = new ZType.ZTypeVar(999) };
        var funcDef = new IrNode.FuncDef(
            "test_func",
            [new IrParam("x", new ZType.ZTypeVar(999))],
            new ZType.ZTypeVar(999),
            unresolvedVar,
            false
        );
        var seq = new IrNode.Seq([funcDef]);
        var (output, diag) = EmitDirect(seq);
        Assert.DoesNotContain(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("Unresolved type variable in C# emission")
        );
        Assert.Contains("int", output);
        Assert.DoesNotContain("object", output);
    }

    [Fact]
    public void ValidCompilation_NoSpuriousWarnings()
    {
        var source =
            @"(module test)
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
        var source =
            @"(module test)
(define-record Point [x : Int] [y : Int])
(define (origin) : Point (Point 0 0))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitUnionInModule_NestedInsideModuleClass()
    {
        var source =
            @"(module test)
(define-union Shape (Circle [r : Float]) (Rect [w : Float] [h : Float]))
(define (unit-circle) : Shape (Circle 1.0))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitClassInModule_NestedInsideModuleClass()
    {
        var source =
            @"(module test)
(define-class Point
  [x : Int]
  [y : Int]
  (define (magnitude) : Int
    (+ (* x x) (* y y))))
(define (make-point) : Point (Point 1 2))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitInterfaceInModule_NestedInsideModuleClass()
    {
        var source =
            @"(module test)
(define-interface IGreeter
  (greet [name : String] : String))
(define (make-greeter) : IGreeter
  (object (IGreeter)
    (define (greet [name : String]) : String name)))";
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void EmitRecordWithoutModule_StaysAtNamespaceLevel()
    {
        var cs = Compile("(define-record Point [x : Int] [y : Int])");
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;

            public sealed record Point(int X, int Y);


            """,
            cs
        );
    }

    [Fact]
    public void EmitTypeOnlyModule_EmitsModuleClass()
    {
        var source =
            @"(module test)
(define-record Point [x : Int] [y : Int])";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public sealed record Point(int X, int Y);

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitVariadicFunction_EmitsParamsKeyword()
    {
        // Variadic params synthesize a Mutable-Vector internally, which only resolves to T[]
        // when the Mutable-Vector alias is in the registry. Importing stdlib/mutable/vector
        // brings in that alias declaration.
        var cs = Compile(
            @"(import stdlib/mutable/vector)
(define (fmt [s : String] [args : String ...]) : String s)"
        );
        Assert.Contains("public static string Fmt(string s, params string[] args)", cs);
    }

    [Fact]
    public void EmitVariadicCall_EmitsArrayConstruction()
    {
        var source =
            @"(import stdlib/mutable/vector)
(define (fmt [s : String] [args : String ...]) : String s)
(fmt ""hello"" ""a"" ""b"")";
        var cs = Compile(source);
        Assert.Contains("public static string Fmt(string s, params string[] args)", cs);
        Assert.Contains("Fmt(\"hello\", new string[] { \"a\", \"b\" })", cs);
    }

    [Fact]
    public void EmitWithHandlers_SingleHandler()
    {
        var source =
            @"(module test)
(define (safe-div [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.DivideByZeroException _] 0)
    (/ a b)))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int SafeDiv(int a, int b)
                {
                    return ((System.Func<int>)(() => { try { return (a / b); } catch (System.DivideByZeroException) { return 0; } }))();
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitWithHandlers_MultipleHandlers()
    {
        var source =
            @"(module test)
(define (f [a : Int] [b : Int]) : Int
  (with-handlers
    ([System.DivideByZeroException _] 0)
    ([System.OverflowException _] -1)
    (/ a b)))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int F(int a, int b)
                {
                    return ((System.Func<int>)(() => { try { return (a / b); } catch (System.DivideByZeroException) { return 0; } catch (System.OverflowException) { return -1; } }))();
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitWithHandlers_DiscardBinding()
    {
        var source =
            @"(module test)
(define (f [x : Int]) : Int
  (with-handlers
    ([System.Exception _] 0)
    x))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static int F(int x)
                {
                    return ((System.Func<int>)(() => { try { return x; } catch (System.Exception) { return 0; } }))();
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitWithHandlers_NamedBinding()
    {
        var source =
            @"(module test)
(import-clr
  [ex-message System.Exception.Message :instance-property : (System.Exception -> String)])

(define (f [x : Int]) : String
  (with-handlers
    ([System.Exception e] (ex-message e))
    (begin x ""ok"")))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static string F(int x)
                {
                    return ((System.Func<string>)(() => { try { return ((System.Func<int, string>)((int _) => "ok"))(x); } catch (System.Exception e) { return e.Message; } }))();
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitWithHandlers_AwaitInBody_EmitsAsyncLambda()
    {
        // Regression: a with-handlers body that contains an `await` used to
        // emit `((Func<int>)(() => { try { return await G(...); } ... }))()`,
        // which fails to compile with CS4034 because the lambda is not async.
        // The fix wraps the try/catch in an `async () => Task<T>` lambda and
        // awaits the call so the awaits run inside the enclosing async method.
        var source =
            @"(module test)
(define-async (g [x : Int]) : (Task Int) x)
(define-async (compute) : (Task Int)
  (with-handlers ([System.Exception e] -1)
    (await (g 42))))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static async System.Threading.Tasks.Task<int> G(int x)
                {
                    return x;
                }

                public static async System.Threading.Tasks.Task<int> Compute()
                {
                    return (await ((System.Func<System.Threading.Tasks.Task<int>>)(async () => { try { return await G(42); } catch (System.Exception e) { return -1; } }))());
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitWithHandlers_AwaitInHandler_EmitsAsyncLambda()
    {
        var source =
            @"(module test)
(define-async (g [x : Int]) : (Task Int) x)
(define-async (compute [n : Int]) : (Task Int)
  (with-handlers ([System.Exception _] (await (g n)))
    n))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static async System.Threading.Tasks.Task<int> G(int x)
                {
                    return x;
                }

                public static async System.Threading.Tasks.Task<int> Compute(int n)
                {
                    return (await ((System.Func<System.Threading.Tasks.Task<int>>)(async () => { try { return n; } catch (System.Exception) { return await G(n); } }))());
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitWithHandlers_NoAwait_StillEmitsSyncLambda()
    {
        // A with-handlers without any await keeps the original sync `Func<T>`
        // emission — only the await-bearing case needs the async wrapper.
        var source =
            @"(module test)
(define-async (compute [a : Int] [b : Int]) : (Task Int)
  (with-handlers ([System.DivideByZeroException _] 0)
    (/ a b)))";
        var cs = Compile(source);
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static async System.Threading.Tasks.Task<int> Compute(int a, int b)
                {
                    return ((System.Func<int>)(() => { try { return (a / b); } catch (System.DivideByZeroException) { return 0; } }))();
                }

            }
            """,
            cs
        );
    }

    // ─── Generic new ─────────────────────────────────────────────────

    [Fact]
    public void EmitClrNew_GenericType()
    {
        // Mutable-Hash alias lives in stdlib; importing it brings the alias into the registry.
        var cs = Compile(
            @"(module test)
(import stdlib/mutable/hash)
(define (make-dict) : (Mutable-Hash String Int)
  (new (System.Collections.Generic.Dictionary String Int)))"
        );
        Assert.Contains(
            "public static System.Collections.Generic.Dictionary<string, int> MakeDict()",
            cs
        );
        Assert.Contains("return new System.Collections.Generic.Dictionary<string, int>();", cs);
    }

    // ─── Generic CLR method type-argument resolution ─────────────────

    [Fact]
    public void EmitGenericClrCall_SerializeRecord_EmitsRecordTypeArg()
    {
        // `^a` appears in argument position, so the type arg is taken from the argument's
        // type (the record W) — not the String return type.
        var cs = Compile(
            @"(module test)
(import-clr
  System.Text.Json
  [json-serialize System.Text.Json.JsonSerializer/Serialize ^a : (^a -> String)])
(define-record W [name : String] [count : Int])
(define (go) : String
  (json-serialize (W ""g"" 7)))"
        );
        Assert.Contains("System.Text.Json.JsonSerializer.Serialize<W>(", cs);
    }

    [Fact]
    public void EmitGenericClrCall_DeserializeRecord_EmitsRecordTypeArg()
    {
        // `^a` appears in return position, so the type arg is taken from the resolved
        // return type (the record W) — not the String argument.
        var cs = Compile(
            @"(module test)
(import-clr
  System.Text.Json
  [json-deserialize System.Text.Json.JsonSerializer/Deserialize ^a : (String -> ^a)])
(define-record W [name : String] [count : Int])
(define (go [s : String]) : W
  (json-deserialize s))"
        );
        Assert.Contains("System.Text.Json.JsonSerializer.Deserialize<W>(", cs);
    }

    // ─── Out parameter support ───────────────────────────────────────

    [Fact]
    public void EmitOutParam_IntTryParse()
    {
        var cs = Compile(
            @"(module test)
(import-clr
  [try-parse System.Int32/TryParse])
(define (test [s : String]) : (ValueTuple Bool Int)
  (try-parse s))"
        );
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public static (bool, int) Test(string s)
                {
                    return ((System.Func<(bool, int)>)(() => { int __out0 = default; var __ret = System.Int32.TryParse(s, out __out0); return (__ret, __out0); }))();
                }

            }
            """,
            cs
        );
    }

    [Fact]
    public void EmitClrNew_InGenericFunction_SubstitutesTypeArgFromResolvedType()
    {
        // Regression: previously `(new (ConcurrentQueue ^a))` inside a polymorphic
        // function emitted `new ConcurrentQueue<A>()` because the IR carried the raw
        // `^a` annotation through to the C# emitter instead of the resolved type-var.
        var cs = Compile(
            @"(module test)
(import-clr
  System.Collections.Concurrent)
(import stdlib/concurrent/queue)
(define (make-queue) : (Concurrent-Queue ^a)
  (new (System.Collections.Concurrent.ConcurrentQueue ^a)))"
        );
        Assert.Contains("return new System.Collections.Concurrent.ConcurrentQueue<T0>();", cs);
        Assert.DoesNotContain("ConcurrentQueue<A>", cs);
        Assert.DoesNotContain("ConcurrentQueue<^a>", cs);
    }

    [Fact]
    public void EmitOutParam_GenericInstanceMethod_DerivesLocalTypeFromCallSite()
    {
        // Regression: out-param locals were typed using the CLR reflection element
        // type `T`, leaving the literal `T` in emitted C# (e.g. `T __out0 = default;`)
        // even though the enclosing method's generic parameter was `T0`. The fix
        // derives the local type from the resolved ValueTuple return type at the
        // call site so it correctly substitutes to `T0`.
        var cs = Compile(
            @"(module test)
(import-clr
  System.Collections.Concurrent
  [cq-try-dequeue System.Collections.Concurrent.ConcurrentQueue.TryDequeue
    :instance : ((Concurrent-Queue ^a) -> (ValueTuple Bool ^a))])
(define (try-deq [q : (Concurrent-Queue ^a)]) : (ValueTuple Bool ^a)
  (cq-try-dequeue q))"
        );
        Assert.Contains("T0 __out0 = default;", cs);
        Assert.DoesNotContain("T __out0 = default;", cs);
    }

    [Fact]
    public void AsyncClassMethod_EmitsAsyncModifier()
    {
        var source = """
            (module test)
            (namespace System.Threading.Tasks)

            (define-interface IWorker
              (DoWork [x : Int] : (Task Int)))

            (define-class Worker : IWorker
              (define-async (DoWork [x : Int]) : (Task Int)
                (+ x 1)))
            """;
        var cs = Compile(source);
        AssertOutput(
            """
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
            """,
            cs
        );
    }

    [Fact]
    public void AsyncClassMethod_WithAwait_EmitsAwait()
    {
        var source = """
            (module test)
            (namespace System.Threading.Tasks)

            (define-async (helper [x : Int]) : (Task Int)
              (+ x 1))

            (define-class Worker
              (define-async (DoWork [x : Int]) : (Task Int)
                (let [result (await (helper x))]
                  (+ result 10))))
            """;
        var cs = Compile(source);
        AssertOutput(
            """
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
                        return (result + 10);
                    }
                }

            }
            """,
            cs
        );
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
        var cs = Compile("(module test)\n(define (f [t : (Int * String)]) : Int (value/0 t))");
        Assert.Contains("(int, string) t", cs);
    }

    [Fact]
    public void EmitTupleAccessor()
    {
        var cs = Compile("(module test)\n(define (f [t : (Int * String)]) : Int (value/0 t))");
        Assert.Contains("t.Item1", cs);
    }

    [Fact]
    public void EmitTupleAccessorSecond()
    {
        var cs = Compile("(module test)\n(define (f [t : (Int * String)]) : String (value/1 t))");
        Assert.Contains("t.Item2", cs);
    }

    [Fact]
    public void EmitTuplePatternMatch()
    {
        var cs = Compile(
            @"(module test)
(define (swap [t : (Int * String)]) : (String * Int)
  (match t
    [(values x y) (values y x)]))"
        );
        Assert.Contains("(var x, var y) =>", cs);
        Assert.Contains("(y, x)", cs);
    }

    [Fact]
    public void EmitTuplePatternMatch_WithNestedGenericRecord()
    {
        // Regression: tuple sub-patterns used to discard the scrutinee type when
        // recursing, so a nested constructor pattern against a generic record
        // emitted `FRec(var x, _)` with no type args. Roslyn rejects that as
        // CS0305 ("requires N type arguments") inside a positional pattern.
        var cs = Compile(
            @"(module test)
(define-record (FRec ^a) [x : ^a] [y : ^a])
(define (compute) : Int
  (match (values (FRec 19 7) 42)
    [(values (FRec a _) b) (+ a b)]
    [_ 0]))"
        );
        Assert.Contains("FRec<int>(var a, _)", cs);
    }

    [Fact]
    public void EmitTupleReturnType()
    {
        var cs = Compile(
            "(module test)\n(define (make [x : Int] [y : String]) : (Int * String) (values x y))"
        );
        Assert.Contains("(int, string) Make", cs);
    }

    [Fact]
    public void EmitThreeElementTuple()
    {
        var cs = Compile("(module test)\n(define triple (values 1 \"a\" #t))");
        Assert.Contains("(1, \"a\", true)", cs);
    }

    [Fact]
    public void EmitNestedTuple()
    {
        var cs = Compile("(module test)\n(define nested (values (values 1 2) (values 3 4)))");
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
    public void EmitClassMethod_CallsModuleLevelDefine()
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
    public void EmitClassMethod_RecursiveCall()
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
    public void EmitMethodCall_NegativeIntLiteralReceiverIsParenthesized()
    {
        // Regression (found by fuzzer seed 0x32444a3a): `(int->string -52468)`
        // lowers to a method call `.ToString()` on the integer literal `-52468`.
        // The emitter previously produced `-52468.ToString()`, which C# parses
        // as `-(52468.ToString())` — Roslyn rejects with CS0023 ("Operator '-'
        // cannot be applied to operand of type 'string'") because `.` and `[]`
        // bind tighter than unary `-`. Wrap negative-literal receivers in
        // parens so member access binds to the negated value.
        var source =
            @"(module test)
(define (compute) : String
  (int->string -52468))";
        var cs = Compile(source);
        Assert.Contains("(-52468).ToString()", cs);
        Assert.DoesNotContain("-52468.ToString()", cs);
    }

    [Fact]
    public void EmitMethodCall_PositiveIntLiteralReceiverIsNotParenthesized()
    {
        // Sibling case: a non-negative receiver should not pick up redundant
        // parens — `52468.ToString()` is unambiguous and the fix should leave
        // it alone.
        var source =
            @"(module test)
(define (compute) : String
  (int->string 52468))";
        var cs = Compile(source);
        Assert.Contains("52468.ToString()", cs);
        Assert.DoesNotContain("(52468).ToString()", cs);
    }

    [Fact]
    public void EmitMethodCall_IntMinValueReceiverIsNotParenthesized()
    {
        // `int.MinValue` is emitted instead of `-2147483648` (see
        // FormatIntLiteral) so it does not start with `-` and needs no
        // wrapping. Guard against future regressions of the parenthesization
        // rule that could accidentally cover this identifier.
        var source =
            @"(module test)
(define (compute) : String
  (int->string -2147483648))";
        var cs = Compile(source);
        Assert.Contains("int.MinValue.ToString()", cs);
        Assert.DoesNotContain("(int.MinValue).ToString()", cs);
    }

    [Fact]
    public void EmitMethodCall_MatchExpressionReceiverIsParenthesized()
    {
        // Regression (found by fuzzer seed 0xff4dea05, case 0xde807f8e):
        // `(int->string (match ...))` lowers to a method call `.ToString()`
        // whose receiver is an `IrNode.Match`, emitted as `<scrut> switch
        // { ... }`. Without parens, Roslyn rejects with CS1003 "',' expected"
        // — the C# parser does not recognize `<switch>.M()` as a member
        // access on the switch result; the `.M()` is interpreted as part of
        // the last switch arm body and the closing `}` ends the arm list
        // unexpectedly. Wrap match-expression receivers in parens so member
        // access binds to the switch result.
        var source =
            @"(module test)
(define (compute [p0 : Int]) : String
  (int->string (match 43 [-2 p0] [x42 41])))";
        var cs = Compile(source);
        Assert.Contains("(43 switch {", cs);
        Assert.Contains("}).ToString()", cs);
        Assert.DoesNotContain("} switch", cs);
        Assert.DoesNotContain("}.ToString()", cs);
    }

    [Fact]
    public void EmitFunctionWithBangSuffix_SanitizesIdentifier()
    {
        // Regression (found by fuzzer seed 0x9305e295): a stdlib module
        // bundled into the C# output exported a function named `set!`, which
        // the C# emitter rendered as the literal identifier `Set!` — `!` is
        // not a legal C# identifier character, so Roslyn rejected the emitted
        // source with CS1003/CS1002. The user program never referenced `set!`
        // directly; the function was pulled in transitively via `stdlib/list`'s
        // import of `stdlib/mutable/vector` and emitted as part of the bundled
        // output. NameConverter must map `!` to a safe sequence so any function
        // whose Scheme name ends in `!` round-trips through codegen.
        var source =
            @"(module test)
(import stdlib/mutable/vector)
(define (touch [xs : (Mutable-Vector Int)] [i : Int] [v : Int]) : Unit
  (vector-set! xs i v))";
        var cs = Compile(source);
        Assert.Contains("VectorSet_b", cs);
        Assert.DoesNotContain("vector-set!", cs);
        Assert.DoesNotContain("Set!", cs);
    }

    [Fact]
    public void EmitMethodCall_AwaitExpressionReceiverIsParenthesized()
    {
        // `await x.M()` parses as `await (x.M())` in C# — the `await` operand
        // is a unary expression and member access binds tighter. When ZScheme
        // lowers `(int->string (await ...))` to a method call `.ToString()`
        // on an `IrNode.Await`, emitting a bare `await ...` as the receiver
        // would re-parent the `.ToString()` onto the awaited Task<int>,
        // producing wrong code (`await Compute(x).ToString()` is not the
        // same as `(await Compute(x)).ToString()`). Wrap await-expression
        // receivers in parens so member access binds to the awaited result.
        var source =
            @"(module test)
(define-async (inner [x : Int]) : (Task Int) (+ x 1))
(define-async (outer [x : Int]) : (Task String)
  (int->string (await (inner x))))";
        var cs = Compile(source);
        Assert.Contains("(await Inner(x)).ToString()", cs);
        Assert.DoesNotContain("await Inner(x).ToString()", cs);
    }

    [Fact]
    public void EmptyVariadicCall_DefaultsParamsArrayElementType_ConsistentlyWithCallSite()
    {
        // Regression: `(list)` with zero args used to lower to a `MutableArrayNew`
        // whose element type stayed as an unresolved type variable. The C# emitter
        // then defaulted that element type to `object` (in EmitMutableArrayNew)
        // while InferCallTypeArgs at the surrounding call site defaulted the
        // generic type argument to `int`, producing
        // `Stdlib_ListModule.List<int>(System.Array.Empty<object>())` — which
        // Roslyn rejects with CS1503: cannot convert from `object[]` to `int[]`.
        // Both sites must agree on `int` so the call is well-typed.
        var source = """
            (module test)
            (import stdlib/list)
            (define-struct R [f0 : Int])
            (define (compute) : Int
              (R/f0 (R (length (list)))))
            """;
        var cs = Compile(source);
        Assert.DoesNotContain("System.Array.Empty<object>()", cs);
        Assert.Contains("System.Array.Empty<int>()", cs);
    }

    [Fact]
    public void EmptyVariadicCall_InsideGenericFunction_KeepsBoundTypeParameter()
    {
        // Counterpart to the above: when an empty `(list)` appears inside a
        // generic function whose type parameter is what the list is generic
        // over, the params array's element type *is* a real bound generic
        // parameter (not a free type variable). It must be emitted as the
        // generic name (e.g. `T0`), not collapsed to `int`. The IsFreeTypeVar
        // check distinguishes bound generic params (in _currentFuncTypeVarMap)
        // from truly unresolved inference variables.
        var source = """
            (module test)
            (import stdlib/list)
            (define (empty-of ^a) : (List ^a)
              (list))
            """;
        var cs = Compile(source);
        Assert.Contains("System.Array.Empty<T0>()", cs);
        Assert.DoesNotContain("System.Array.Empty<int>()", cs);
        Assert.DoesNotContain("System.Array.Empty<object>()", cs);
    }

    [Fact]
    public void OverloadResolvedCall_ToNonGenericFunc_DoesNotEmitTypeArgs()
    {
        // Regression: `stdlib/string` exports a *non-generic* `empty?` (String -> Bool)
        // while `stdlib/list` exports a *generic* `empty?` ((List ^a) -> Bool). The
        // generic-func registry was keyed by bare name, so the list entry won the
        // last-write-wins bare key. When a call was overload-resolved to the string
        // `empty?` (its Var carrying ModuleName), the qualified lookup correctly
        // missed — but the code then fell back to the bare key and found the *list*
        // entry, emitting `Stdlib_StringModule.Empty_q<int>(s)`. Roslyn rejected that
        // with CS0308 ("non-generic method cannot be used with type arguments").
        // The overload-resolved call must NOT borrow another module's type args.
        var source = """
            (module test)
            (import stdlib/string)
            (import stdlib/list)
            (define (check [s : String]) : Bool
              (empty? s))
            """;
        var cs = Compile(source);
        Assert.Contains("Stdlib_StringModule.Empty_q(s)", cs);
        Assert.DoesNotContain("Stdlib_StringModule.Empty_q<", cs);
    }

    [Fact]
    public void OverloadResolvedCall_ToGenericFunc_StillEmitsTypeArgs()
    {
        // Counterpart guard: the fix above (consult only the qualified registry
        // entry for an overload-resolved Var) must not regress the legitimate case
        // where the resolved module's function genuinely IS generic. The list
        // `empty?` still needs explicit type arguments here.
        var source = """
            (module test)
            (import stdlib/string)
            (import stdlib/list)
            (define (check [xs : (List Int)]) : Bool
              (empty? xs))
            """;
        var cs = Compile(source);
        Assert.Contains("Stdlib_ListModule.Empty_q<int>(xs)", cs);
    }

    // ─── Type Alias Emission ────────────────────────────────────
    // These tests verify that ZScheme type aliases resolve to the correct CLR types
    // in the generated C# code.

    [Fact]
    public void Emit_MutableHash_UsesDictionaryClrType()
    {
        var cs = Compile(
            @"(module test)
(import stdlib/mutable/hash)
(define (make-dict) : (Mutable-Hash String Int)
  (new (System.Collections.Generic.Dictionary String Int)))"
        );
        Assert.Contains(
            "public static System.Collections.Generic.Dictionary<string, int> MakeDict()",
            cs
        );
        Assert.Contains("return new System.Collections.Generic.Dictionary<string, int>();", cs);
    }

    [Fact]
    public void Emit_MutableList_UsesListClrType()
    {
        var cs = Compile(
            @"(module test)
(import stdlib/mutable/treelist)
(define (make-list) : (Mutable-TreeList Int)
  (new (System.Collections.Generic.List Int)))"
        );
        Assert.Contains("public static System.Collections.Generic.List<int> MakeList()", cs);
        Assert.Contains("return new System.Collections.Generic.List<int>();", cs);
    }

    [Fact]
    public void Emit_Hash_UsesImmutableDictionaryClrType()
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
    public void Emit_Vector_UsesImmutableArrayClrType()
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
    public void Emit_List_UsesImmutableListClrType()
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
    public void Emit_ConcurrentQueue_UsesConcurrentQueueClrType()
    {
        var cs = Compile(
            @"(module test)
(import stdlib/concurrent/queue)
(import-clr System.Collections.Concurrent)
(define (make-queue) : (Concurrent-Queue Int)
  (new (System.Collections.Concurrent.ConcurrentQueue Int)))"
        );
        Assert.Contains(
            "public static System.Collections.Concurrent.ConcurrentQueue<int> MakeQueue()",
            cs
        );
    }

    [Fact]
    public void Emit_ConcurrentDictionary_UsesConcurrentDictionaryClrType()
    {
        var cs = Compile(
            @"(module test)
(import stdlib/concurrent/dictionary)
(import-clr System.Collections.Concurrent)
(define (make-dict) : (Concurrent-Dictionary String Int)
  (new (System.Collections.Concurrent.ConcurrentDictionary String Int)))"
        );
        Assert.Contains(
            "public static System.Collections.Concurrent.ConcurrentDictionary<string, int> MakeDict()",
            cs
        );
    }

    [Fact]
    public void Emit_NestedAliases_ResolvesAllLevels()
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
    public void Emit_FunctionParameterAlias_UsesClrTypeInSignature()
    {
        var cs = Compile(
            @"(module test)
(import stdlib/mutable/treelist)
(define (add-item [lst : (Mutable-TreeList Int)] [x : Int]) : Unit
  ())"
        );
        Assert.Contains(
            "public static void AddItem(System.Collections.Generic.List<int> lst, int x)",
            cs
        );
    }

    [Fact]
    public void Emit_DelegateTypeAnnotation_UsesClrTypeName()
    {
        var cs = Compile(
            @"(module test)
(define (set-handler [h : (delegate System.Action)]) : Unit
  ())"
        );
        Assert.Contains("public static void SetHandler(System.Action h)", cs);
    }

    [Fact]
    public void Emit_DelegateTypeInLambdaParam_UsesClrTypeName()
    {
        var cs = Compile(
            @"(module test)
(define (make-callback [handler : (delegate System.Action)]) : Unit
  ())"
        );
        Assert.Contains("public static void MakeCallback(System.Action handler)", cs);
    }

    [Fact]
    public void Emit_DelegateTypeParameter_UsesClrTypeNameInSignature()
    {
        var cs = Compile(
            @"(module test)
(define (register [handler : (delegate System.Action)]) : Unit
  ())"
        );
        Assert.Contains("public static void Register(System.Action handler)", cs);
    }

    // A function whose sanitized C# name matches a type declared in the same
    // module class would emit a nested type and a method with the same name,
    // which Roslyn rejects with CS0102 (the CLR/IL backend tolerates it, which
    // is why this only surfaced through the C# backend). The function must be
    // renamed while the type keeps its name. Found by the fuzzer.
    [Fact]
    public void UnionTypeNameCollidingWithFunction_RenamesFunctionNotType()
    {
        var cs = Compile(
            @"(module test)
(define-union (Box ^a)
  (Wrap [value : ^a]))
(define (box [x : ^a]) : (Box ^a) (Wrap x))
(define (use) : (Box Int) (box 5))"
        );
        AssertOutput(
            """
            #nullable enable

            namespace ZSchemeGenerated;


            public static class TestModule
            {
                public abstract record Box<T0>;
                public sealed record Wrap<T0>(T0 Value) : Box<T0>;


                public static Box<T0> Box_fn<T0>(T0 x)
                {
                    return new Wrap<T0>(x);
                }

                public static Box<int> Use()
                {
                    return Box_fn<int>(5);
                }

            }
            """,
            cs
        );
    }

    // The same collision can arise between a function and a union *case*
    // (constructor), which is also emitted as a nested record. The constructor
    // keeps its name; the function is renamed.
    [Fact]
    public void UnionCaseNameCollidingWithFunction_RenamesFunction()
    {
        var cs = Compile(
            @"(module test)
(define-union (Box ^a)
  (Wrap [value : ^a]))
(define (wrap [x : ^a]) : (Box ^a) (Wrap x))
(define (use) : (Box Int) (wrap 7))"
        );
        // Constructor record keeps its name and is still used verbatim.
        Assert.Contains("public sealed record Wrap<T0>(T0 Value) : Box<T0>;", cs);
        Assert.Contains("return new Wrap<T0>(x);", cs);
        // The function is renamed at both definition and call site.
        Assert.Contains("public static Box<T0> Wrap_fn<T0>(T0 x)", cs);
        Assert.Contains("return Wrap_fn<int>(7);", cs);
        // The function must not be defined under the colliding bare name.
        Assert.DoesNotContain("public static Box<T0> Wrap<T0>(T0 x)", cs);
    }

    // End-to-end against the real stdlib: the `list` module declares both a
    // `List` union type (with a `Cons` case) and `list`/`cons` functions, all of
    // which sanitize into the same C# class. The functions must be renamed so
    // `Stdlib_ListModule` does not redefine `List`/`Cons`. This is the exact
    // shape the fuzzer surfaced.
    [Fact]
    public void ImportingStdlibList_RenamesListAndConsFunctionsAvoidingTypeCollision()
    {
        var cs = Compile(
            @"(module test)
(import stdlib/list)
(define (go) : Int
  (length (cons 1 (list 2 3))))"
        );
        // The union type and its case are declared with their original names.
        Assert.Contains("public abstract record List<", cs);
        Assert.Contains("Cons<", cs);
        // The colliding functions are renamed at definition and call sites.
        Assert.Contains("List_fn<", cs);
        Assert.Contains("Cons_fn<", cs);
        // No method shares a bare name with the nested type (would be CS0102).
        Assert.DoesNotContain("List<T0> List<T0>(params", cs);
        Assert.DoesNotContain("List<T0> Cons<T0>(T0", cs);
    }

    // Cross-module inheritance: module A declares a type, module B in the same
    // compilation inherits from / implements it. The declaring module's `.zs` is
    // written to a temp dir that is added to the search path, so `(import ...)`
    // resolves it. The emitted C# (declaring module emitted inline as a nested
    // static class) is fed through Roslyn, which is what catches an unqualified
    // base/interface name (CS0246).
    private static string CompileCrossModule(
        string moduleAName,
        string moduleASource,
        string mainSource,
        bool verifyCompiles = true,
        [CallerMemberName] string caller = ""
    )
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, moduleAName + ".zs"), moduleASource);
            var mainPath = Path.Combine(dir, "main.zs");
            File.WriteAllText(mainPath, mainSource);

            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.CSharp,
                    SuppressVersionPreamble = true,
                    DisablePrelude = true,
                    PackagePaths = new Dictionary<string, string>
                    {
                        ["stdlib"] = GetStdLibPath(),
                        ["zunit"] = GetZUnitPath(),
                    },
                    ModuleSearchPaths = [dir, GetZUnitPath()],
                    ModuleAliases = new Dictionary<string, string> { ["zunit"] = "zunit/zunit" },
                }
            );
            var result = compilation.Compile(mainSource, mainPath);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));
            var csResult = (CompilationResult.CSharpOutputResult)result;
            if (verifyCompiles && !KnownNonCompilingOutput.Contains(caller))
                RoslynCompileVerifier.AssertCompiles(
                    csResult.CsOutput,
                    csResult.PrecompiledAssemblyPaths
                );
            return csResult.CsOutput;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // An object expression implementing an interface declared in another module
    // must emit the module-qualified interface name (e.g. `IfaceModModule.IProcessor`),
    // not a bare `IProcessor` that lives in a different nested static class.
    [Fact]
    public void EmitObjectExpr_ImplementsCrossModuleInterface()
    {
        var cs = CompileCrossModule(
            "iface-mod",
            @"(module iface-mod)
(export IProcessor)
(define-interface IProcessor
  (Process [x : Int] : Int))",
            @"(module main)
(import iface-mod)
(define (make-processor) : IProcessor
  (object IProcessor
    (define (Process [x : Int]) : Int (* x 2))))"
        );
        Assert.Contains("IfaceModModule.IProcessor", cs);
    }

    // A class declaration extending a base class and implementing an interface,
    // both declared in another module, must qualify both names.
    [Fact]
    public void EmitClassDecl_ExtendsAndImplementsCrossModule()
    {
        var cs = CompileCrossModule(
            "base-mod",
            @"(module base-mod)
(export Base IService)
(define-class #:open Base
  [name : String]
  (define (GetName) : String name))
(define-interface IService
  (Describe [] : String))",
            @"(module main)
(import base-mod)
(define-class Impl : Base IService
  (constructor (super ""impl""))
  (define (Describe) : String ""impl-service""))"
        );
        Assert.Contains("BaseModModule.Base", cs);
        Assert.Contains("BaseModModule.IService", cs);
    }

    // An interface extending a base interface declared in another module must
    // qualify the base-interface name.
    [Fact]
    public void EmitInterfaceDecl_ExtendsCrossModuleBaseInterface()
    {
        var cs = CompileCrossModule(
            "base-iface-mod",
            @"(module base-iface-mod)
(export IBase)
(define-interface IBase
  (Foo [] : Int))",
            @"(module main)
(import base-iface-mod)
(define-interface IDerived : IBase
  (Bar [] : Int))"
        );
        Assert.Contains("BaseIfaceModModule.IBase", cs);
    }

    // A Unit-returning lambda whose body is the Unit literal `()` previously
    // emitted `(System.Action)(() => { default(System.ValueTuple); })`. A bare
    // `default(...)` is a value expression, not a legal C# statement, so Roslyn
    // rejected it with CS0201. The Unit literal has no side effect and must be
    // elided, leaving an empty block. Found by the fuzzer (diffexec / CS0201).
    [Fact]
    public void UnitReturningLambda_WithUnitLiteralBody_ElidesStatement()
    {
        var cs = Compile(
            @"(module test)
(define (run-action [a : (delegate System.Action)]) : Unit
  (a))
(define (go) : Unit
  (run-action (lambda () ())))"
        );
        // The lambda body collapses to an empty block, never a bare
        // `default(System.ValueTuple);` statement.
        Assert.Contains("(() => {  })", cs);
        Assert.DoesNotContain("{ default(System.ValueTuple); }", cs);
    }

    // A Unit-returning lambda whose body is a value-producing Unit expression
    // (here an `if`, which lowers to a C# ternary) is likewise not a legal bare
    // statement (CS0201). It must be discarded via `_ = ...;`. Found by the
    // fuzzer (the same family as the empty-Action case).
    [Fact]
    public void UnitReturningLambda_WithTernaryBody_DiscardsViaUnderscore()
    {
        var cs = Compile(
            @"(module test)
(define (run-action [a : (delegate System.Action)]) : Unit
  (a))
(define (go) : Unit
  (run-action (lambda () (if #t () ()))))"
        );
        Assert.Contains("_ = (true ?", cs);
    }

    // A Unit-returning *function* whose body is a value-producing Unit expression
    // (an `if` → ternary) hits the same CS0201 hazard through EmitUnitStatement
    // and must also be discarded via `_ = ...;`.
    [Fact]
    public void UnitReturningFunction_WithTernaryBody_DiscardsViaUnderscore()
    {
        var cs = Compile(
            @"(module test)
(define (act) : Unit
  (if #t () ()))"
        );
        Assert.Contains("_ = (true ?", cs);
    }
}
