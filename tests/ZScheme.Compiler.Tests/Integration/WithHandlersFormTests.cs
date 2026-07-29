using System.Reflection;
using Xunit;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

// Shape + behaviour tests for `(with-handlers ...)` in *statement* position. C# has no
// try/catch expression, so a with-handlers used as a genuine subexpression must stay wrapped
// in an immediately-invoked lambda (`((System.Func<T>)(() => { try … }))()`). Everywhere a C#
// statement is legal, though, it must emit a bare try/catch instead — the same treatment
// `(use ...)` gets in UseFormTests. Each test therefore asserts both that the emitted C#
// contains no IIFE cast and that the program still behaves correctly, round-tripping through
// Roslyn (and, for the top-level cases, through the IL backend as well).
public class WithHandlersFormTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(WithHandlersFormTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static CompilationResult CompileWith(string source, OutputMode mode)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = mode,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        return compilation.Compile(source);
    }

    private static string CompileCSharp(string source)
    {
        var result = CompileWith(source, OutputMode.CSharp);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return ((CompilationResult.CSharpOutputResult)result).CsOutput;
    }

    private static int CompileIlAndRunInt(string source, string methodName = "Compute")
    {
        var result = CompileWith(source, OutputMode.Il);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return InvokeInt(
            Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes),
            methodName
        );
    }

    private static int CompileCSharpAndRunInt(string source, string methodName = "Compute") =>
        InvokeInt(RoslynCompile(CompileCSharp(source)), methodName);

    // Compiles emitted C# into an in-memory assembly via Roslyn and loads it. Roslyn accepting
    // the output is half the point of these tests: the flattened forms declare a local and
    // assign it inside a try/catch, which only compiles if every catch clause assigns too.
    private static Assembly RoslynCompile(string cs)
    {
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
            "ZSchemeWithHandlersExec",
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
        return Assembly.Load(ms.ToArray());
    }

    private static MethodInfo FindMethod(Assembly asm, string methodName) =>
        asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );

    private static int InvokeInt(Assembly asm, string methodName)
    {
        try
        {
            return (int)FindMethod(asm, methodName).Invoke(null, null)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    private static int AwaitInt(Assembly asm, string methodName)
    {
        try
        {
            var task = (System.Threading.Tasks.Task<int>)
                FindMethod(asm, methodName).Invoke(null, null)!;
            return task.GetAwaiter().GetResult();
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    // Slices out the emitted body of `Compute`. Shape assertions must look only at the
    // module's own method: these compilations inline the stdlib modules too, and those
    // legitimately declare `System.Func<>` parameters. Brace counting is safe here because
    // none of the test sources produce a string literal containing a brace.
    private static string ComputeBody(string cs)
    {
        var sig = cs.IndexOf(" Compute(", StringComparison.Ordinal);
        Assert.True(sig >= 0, $"no Compute method in emitted C#:\n{cs}");
        var open = cs.IndexOf('{', sig);
        var depth = 0;
        for (var i = open; i < cs.Length; i++)
        {
            if (cs[i] == '{')
                depth++;
            else if (cs[i] == '}' && --depth == 0)
                return cs[(open + 1)..i];
        }
        Assert.Fail($"unbalanced braces after Compute in:\n{cs}");
        return "";
    }

    // Asserts the body opens exactly `expected` bare `try` blocks and wraps none of them in an
    // immediately-invoked lambda. `((System.Func<` is precisely the cast EmitWithHandlers /
    // EmitLetExpr emit for an IIFE, so its absence is the real signal — `try` alone would also
    // match the inside of a lambda-wrapped form.
    private static void AssertFlatTryCatch(string cs, int expected)
    {
        var body = ComputeBody(cs);
        Assert.Equal(expected, body.Split("try").Length - 1);
        Assert.DoesNotContain("((System.Func<", body);
    }

    // ---- `let` value position: the with-handlers is not on the function's tail spine, so
    // before this it went through EmitExpr and became an IIFE. It must instead declare the
    // local first and assign it from every leaf of the try and of every catch.

    private const string LetValueSource =
        "(module test)\n"
        + @"(define (div [a : Int] [b : Int]) : Int
  (let ([r (with-handlers ([System.DivideByZeroException _] -1)
             (/ a b))])
    (+ r 100)))
(define (compute) : Int
  (+ (div 10 2) (div 10 0)))";

    [Fact]
    public void WithHandlers_InLetValue_EmitsFlatTryCatch_NotIife()
    {
        var cs = CompileCSharp(LetValueSource);
        Assert.DoesNotContain("((System.Func<", cs);
        Assert.Contains("int r;", cs);
    }

    // 105 (10/2 + 100) + 99 (-1 + 100).
    [Fact]
    public void WithHandlers_InLetValue_Runs_CSharp()
    {
        Assert.Equal(204, CompileCSharpAndRunInt(LetValueSource));
    }

    [Fact]
    public void WithHandlers_InLetValue_Runs_Il()
    {
        Assert.Equal(204, CompileIlAndRunInt(LetValueSource));
    }

    // A `let` *inside* the try must flatten too, rather than reintroducing an IIFE for the
    // inner binding: the assigning walker recurses the whole spine.
    [Fact]
    public void WithHandlers_LetInsideTry_Flattens()
    {
        var source =
            "(module test)\n"
            + @"(define (compute) : Int
  (let ([r (with-handlers ([System.DivideByZeroException _] -1)
             (let ([q (/ 10 5)])
               (* q 3)))])
    r))";
        var cs = CompileCSharp(source);
        AssertFlatTryCatch(cs, 1);
        Assert.Equal(6, InvokeInt(RoslynCompile(cs), "Compute"));
    }

    // The real-world shape: stdlib's `catch` macro expands straight to a with-handlers, so
    // `(let ([r (catch ...)]) ...)` is how nearly all ZScheme code writes this form.
    [Fact]
    public void CatchMacro_InLetValue_EmitsFlatTryCatch_NotIife()
    {
        var source =
            "(module test)\n"
            + "(import stdlib/catch stdlib/result)\n"
            + @"(define (compute) : Int
  (let ([r (catch (/ 10 0))])
    (match r
      [(Ok v) v]
      [(Err _) 42])))";
        var cs = CompileCSharp(source);
        Assert.DoesNotContain("((System.Func<", ComputeBody(cs));
        Assert.Equal(42, InvokeInt(RoslynCompile(cs), "Compute"));
    }

    // A discarded (`_`) binding declares no local at all — `(begin (with-handlers ...) rest)`
    // desugars to exactly that — so the try/catch runs purely for effect.
    [Fact]
    public void WithHandlers_InBeginPosition_EmitsFlatTryCatch_NotIife()
    {
        var source =
            "(module test)\n"
            + @"(define (compute) : Int
  (begin
    (with-handlers ([System.DivideByZeroException _] 0) (/ 10 0))
    7))";
        var cs = CompileCSharp(source);
        AssertFlatTryCatch(cs, 1);
        Assert.Equal(7, InvokeInt(RoslynCompile(cs), "Compute"));
    }

    // An async function whose body contains no `await` anywhere used to skip statement form
    // entirely because EmitFuncDef's guard was `!func.IsAsync && WantsStatementForm(...)`.
    [Fact]
    public void WithHandlers_AsyncNoAwait_EmitsFlatTryCatch()
    {
        var source =
            "(module test)\n"
            + @"(define-async (compute) : (Task Int)
  (with-handlers ([System.DivideByZeroException _] 5)
    (/ 10 0)))";
        var cs = CompileCSharp(source);
        AssertFlatTryCatch(cs, 1);
        Assert.Equal(5, AwaitInt(RoslynCompile(cs), "Compute"));
    }

    // An awaiting body in let-value position must stay in statement form: rendering it as an
    // expression would wrap the `await` in a non-async lambda (CS4034). Roslyn accepting the
    // output and the awaited result being right is the real assertion.
    private const string AsyncLetValueSource =
        "(module test)\n"
        + "(import-clr\n"
        + "  [task-delay System.Threading.Tasks.Task/Delay : (Int -> System.Threading.Tasks.Task)])\n"
        + @"(define-async (compute) : (Task Int)
  (let ([r (with-handlers ([System.DivideByZeroException _] -1)
             (begin (await (task-delay 1)) 9))])
    (+ r 1)))";

    [Fact]
    public void AsyncWithHandlers_InLetValue_EmitsFlatTryCatch_CSharp()
    {
        var cs = CompileCSharp(AsyncLetValueSource);
        Assert.DoesNotContain("((System.Func<", ComputeBody(cs));
        Assert.Equal(10, AwaitInt(RoslynCompile(cs), "Compute"));
    }

    // ---- Bare top-level `with-handlers`: a statement in its own right, run for effect in the
    // module's static constructor. Both backends' top-level collectors had no case for it, so
    // the entire form — body and every handler alike — was dropped with no diagnostic.

    // The resource is a top-level binding, so `compute` observes after the fact whether the
    // form ran: the body disposes it and then divides by zero, which the handler swallows.
    // A dropped form leaves the stream open and yields 0; a handler that failed to catch
    // would instead surface as a TypeInitializationException from the static constructor.
    private const string TopLevelSource =
        "(module test)\n"
        + "(import-clr\n"
        + "  [ms-can-read System.IO.MemoryStream.CanRead :instance-property : (System.IO.MemoryStream -> Bool)]\n"
        + "  [ms-dispose System.IO.MemoryStream.Dispose :instance : (System.IO.MemoryStream -> Unit)])\n"
        + @"(define s (new System.IO.MemoryStream))
(with-handlers ([System.DivideByZeroException _] 0)
  (begin (ms-dispose s) (/ 10 0)))
(define (compute) : Int
  (if (ms-can-read s) 0 1))";

    [Fact]
    public void TopLevelWithHandlers_Runs_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndRunInt(TopLevelSource));
    }

    [Fact]
    public void TopLevelWithHandlers_Runs_Il()
    {
        Assert.Equal(1, CompileIlAndRunInt(TopLevelSource));
    }

    // The static constructor is a statement context, so the top-level form emits a bare
    // try/catch there rather than an immediately-invoked lambda.
    [Fact]
    public void TopLevelWithHandlers_EmitsFlatTryCatchInStaticConstructor()
    {
        var cs = CompileCSharp(TopLevelSource);
        Assert.Contains("static TestModule()", cs);
        Assert.Contains("catch (System.DivideByZeroException)", cs);
    }

    // A top-level with-handlers is the module's only content. It must still count as content —
    // otherwise the module class (and with it the static constructor) is never emitted.
    [Fact]
    public void TopLevelWithHandlers_AloneStillEmitsModuleClass()
    {
        var source =
            "(module test)\n" + @"(with-handlers ([System.DivideByZeroException _] 0) (/ 10 0))";
        var cs = CompileCSharp(source);
        Assert.Contains("class TestModule", cs);
        Assert.Contains("static TestModule()", cs);
        Assert.Contains("catch (System.DivideByZeroException)", cs);
    }

    // ---- Nesting with `use`: each form's statement walker must recurse into the other's
    // bodies rather than falling back to an expression at the boundary.

    [Fact]
    public void WithHandlersInsideUse_BothFlatten()
    {
        var source =
            "(module test)\n"
            + @"(define (compute) : Int
  (use ([m (new System.IO.MemoryStream)])
    (with-handlers ([System.DivideByZeroException _] 3)
      (/ 10 0))))";
        var cs = CompileCSharp(source);
        var body = ComputeBody(cs);
        Assert.DoesNotContain("((System.Func<", body);
        Assert.Contains("using (", body);
        Assert.Contains("catch (System.DivideByZeroException)", body);
        Assert.Equal(3, InvokeInt(RoslynCompile(cs), "Compute"));
    }

    [Fact]
    public void UseInsideWithHandlers_BothFlatten()
    {
        var source =
            "(module test)\n"
            + @"(define (compute) : Int
  (let ([r (with-handlers ([System.DivideByZeroException _] 4)
             (use ([m (new System.IO.MemoryStream)])
               (/ 10 0)))])
    r))";
        var cs = CompileCSharp(source);
        var body = ComputeBody(cs);
        Assert.DoesNotContain("((System.Func<", body);
        Assert.Contains("using (", body);
        Assert.Equal(4, InvokeInt(RoslynCompile(cs), "Compute"));
    }

    // ---- The guard rail: a genuine subexpression has no statement position to flatten into,
    // so the IIFE must stay. C# has no try/catch expression.

    [Fact]
    public void WithHandlers_ExpressionPosition_KeepsIife()
    {
        var source =
            "(module test)\n"
            + @"(define (compute) : Int
  (+ 1 (with-handlers ([System.DivideByZeroException _] 0) (/ 10 0))))";
        var cs = CompileCSharp(source);
        Assert.Contains("((System.Func<int>)", ComputeBody(cs));
        Assert.Equal(1, InvokeInt(RoslynCompile(cs), "Compute"));
    }
}
