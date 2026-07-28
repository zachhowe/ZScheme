using System.Reflection;
using Xunit;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

// End-to-end tests for the `(use ...)` / `(use* ...)` special forms (deterministic
// disposal). Disposal is observed at runtime through System.IO.MemoryStream: a
// disposed MemoryStream returns false from CanRead, so a program can return 1 when
// its resource was disposed and 0 when it was not. Both backends are exercised
// because disposal is emitted differently (native C# `using` vs. an IL try/finally).
public class UseFormTests
{
    // A program that imports MemoryStream.CanRead so disposal is observable.
    private const string CanReadImport =
        "(import-clr\n"
        + "  [ms-can-read System.IO.MemoryStream.CanRead :instance-property : (System.IO.MemoryStream -> Bool)])\n";

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(UseFormTests).Assembly.Location)!;
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
        var ilResult = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(ilResult.OutputBytes);
        return InvokeInt(asm, methodName);
    }

    private static int CompileCSharpAndRunInt(string source, string methodName = "Compute")
    {
        return InvokeInt(RoslynCompile(CompileCSharp(source)), methodName);
    }

    private static int CompileIlAndAwaitInt(string source, string methodName = "Compute")
    {
        var result = CompileWith(source, OutputMode.Il);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        var ilResult = (CompilationResult.IlOutputResult)result;
        return AwaitInt(Assembly.Load(ilResult.OutputBytes), methodName);
    }

    private static int CompileCSharpAndAwaitInt(string source, string methodName = "Compute")
    {
        return AwaitInt(RoslynCompile(CompileCSharp(source)), methodName);
    }

    // Compiles emitted C# into an in-memory assembly via Roslyn and loads it.
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
            "ZSchemeUseExec",
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

    private static System.Reflection.MethodInfo FindMethod(Assembly asm, string methodName)
    {
        return asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
    }

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

    // Invokes a zero-arg Task<int>-returning method and awaits it, unwrapping the
    // user-program exception so Assert.Throws sees the real type.
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
    // legitimately declare `System.Func<>` parameters. Brace counting is safe here
    // because none of the test sources produce a string literal containing a brace.
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

    // Asserts the body opens exactly `expected` native `using` statements and wraps none
    // of them in an immediately-invoked lambda. `((System.Func<` is precisely the cast
    // EmitUseExpr/EmitLetExpr emit for an IIFE, so its absence is the real signal.
    private static void AssertNativeUsings(string cs, int expected)
    {
        var body = ComputeBody(cs);
        Assert.Equal(expected, body.Split("using (").Length - 1);
        Assert.DoesNotContain("((System.Func<", body);
    }

    // The `use` is in tail position of the function body, which has always taken the
    // statement path. Asserting the absence of an IIFE cast is what makes this a shape
    // test rather than a substring an immediately-invoked lambda would also satisfy.
    [Fact]
    public void Use_EmitsNativeCSharpUsing()
    {
        var source =
            @"(module test)
(define (compute) : System.IO.MemoryStream
  (use ([m (new System.IO.MemoryStream)]) m))";
        AssertNativeUsings(CompileCSharp(source), 1);
    }

    // A `use` bound by a `let` is in *value* position, not on the function's tail spine.
    // It must still emit a bare `using` statement — the local is declared first and
    // assigned inside the block — rather than an immediately-invoked lambda.
    [Fact]
    public void Use_InLetValue_EmitsNativeUsing_NotIife()
    {
        AssertNativeUsings(CompileCSharp(DisposeOnReturnSource), 1);
    }

    // `(begin (use* ...) rest)` desugars to a discarded (`_`) let whose value is a use;
    // both resources must emit as nested `using` statements with no lambda wrapper.
    [Fact]
    public void UseStar_InBeginPosition_EmitsNativeUsing_NotIife()
    {
        AssertNativeUsings(CompileCSharp(UseStarSource), 2);
    }

    // A `use` whose body is a genuinely void CLR call is Unit-typed, so the lifted
    // statement form cannot assign the body's value to the let local directly (CS0201) —
    // it runs the call for effect and assigns the unit value. Round-trip through Roslyn
    // to prove that path emits legal C#.
    [Fact]
    public void Use_UnitBodyInLetValue_Compiles()
    {
        var source =
            "(module test)\n"
            + "(import-clr\n"
            + "  [ms-can-read System.IO.MemoryStream.CanRead :instance-property : (System.IO.MemoryStream -> Bool)]\n"
            + "  [ms-flush System.IO.MemoryStream.Flush :instance : (System.IO.MemoryStream -> Unit)])\n"
            + @"(define (compute) : Int
  (let ([s (new System.IO.MemoryStream)])
    (let ([u (use ([m s]) (ms-flush m))])
      (if (ms-can-read s) 0 1))))";
        var cs = CompileCSharp(source);
        AssertNativeUsings(cs, 1);
        Assert.Equal(1, InvokeInt(RoslynCompile(cs), "Compute"));
    }

    // An awaiting `use` body in let-value position must stay in statement form: emitting
    // it as an expression would wrap the `await` in a non-async lambda (CS4034). Roslyn
    // compiling and the awaited result being correct is the real assertion here.
    private const string AsyncUseInLetValueSource =
        "(module test)\n"
        + AsyncImports
        + @"(define-async (compute) : (Task Int)
  (let ([s (new System.IO.MemoryStream)])
    (let ([r (use ([m s]) (begin (await (task-delay 1)) 7))])
      (if (ms-can-read s) 0 r))))";

    [Fact]
    public void AsyncUse_InLetValue_EmitsNativeUsing_CSharp()
    {
        var cs = CompileCSharp(AsyncUseInLetValueSource);
        AssertNativeUsings(cs, 1);
        Assert.Equal(7, AwaitInt(RoslynCompile(cs), "Compute"));
    }

    [Fact]
    public void AsyncUse_InLetValue_Il()
    {
        Assert.Equal(7, CompileIlAndAwaitInt(AsyncUseInLetValueSource));
    }

    // ---- Bare top-level `use`: a statement in its own right, run for effect in the
    // module's static constructor. Both backends' top-level collectors used to have no
    // case for it, so the entire form — resource and body alike — was dropped with no
    // diagnostic: the resource was never created and the body never ran.

    // The resource is a top-level binding, so `compute` can observe it after the
    // top-level `use` scope has exited. Returns 1 only if the `use` actually ran and
    // disposed it; a dropped form leaves the stream open and yields 0.
    private const string TopLevelUseSource =
        "(module test)\n"
        + CanReadImport
        + @"(define s (new System.IO.MemoryStream))
(use ([m s]) 0)
(define (compute) : Int
  (if (ms-can-read s) 0 1))";

    [Fact]
    public void TopLevelUse_RunsAndDisposes_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndRunInt(TopLevelUseSource));
    }

    [Fact]
    public void TopLevelUse_RunsAndDisposes_Il()
    {
        Assert.Equal(1, CompileIlAndRunInt(TopLevelUseSource));
    }

    // The static constructor is a statement context, so the top-level `use` emits a
    // native `using` there rather than an immediately-invoked lambda.
    [Fact]
    public void TopLevelUse_EmitsNativeUsingInStaticConstructor()
    {
        var cs = CompileCSharp(TopLevelUseSource);
        Assert.Contains("static TestModule()", cs);
        Assert.Contains("using (", cs);
        Assert.DoesNotContain("((System.Func<", cs);
    }

    // A top-level `use` is the module's only content. It must still count as content —
    // otherwise the module class (and with it the static constructor) is never emitted.
    [Fact]
    public void TopLevelUse_AloneStillEmitsModuleClass()
    {
        var source =
            @"(module test)
(use ([m (new System.IO.MemoryStream)]) 0)";
        var cs = CompileCSharp(source);
        Assert.Contains("class TestModule", cs);
        Assert.Contains("using (", cs);
    }

    // use* at top level: both resources must be created and disposed, innermost first.
    private const string TopLevelUseStarSource =
        "(module test)\n"
        + CanReadImport
        + @"(define a (new System.IO.MemoryStream))
(define b (new System.IO.MemoryStream))
(use* ([x a] [y b]) 0)
(define (compute) : Int
  (if (ms-can-read a) 0 (if (ms-can-read b) 0 1)))";

    [Fact]
    public void TopLevelUseStar_RunsAndDisposesAll_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndRunInt(TopLevelUseStarSource));
    }

    [Fact]
    public void TopLevelUseStar_RunsAndDisposesAll_Il()
    {
        Assert.Equal(1, CompileIlAndRunInt(TopLevelUseStarSource));
    }

    // The resource is returned from the `use`, so the caller observes it *after*
    // the scope exits — CanRead is false iff it was disposed. Both backends.
    private const string DisposeOnReturnSource =
        "(module test)\n"
        + CanReadImport
        + @"(define (compute) : Int
  (let ([s (use ([m (new System.IO.MemoryStream)]) m)])
    (if (ms-can-read s) 0 1)))";

    [Fact]
    public void Use_DisposesResource_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndRunInt(DisposeOnReturnSource));
    }

    [Fact]
    public void Use_DisposesResource_Il()
    {
        Assert.Equal(1, CompileIlAndRunInt(DisposeOnReturnSource));
    }

    // The resource is created in an outer let (so it survives into the handler),
    // bound by `use`, and the body throws. Disposal must still run via the finally,
    // so the handler sees a disposed stream (returns 1).
    private const string DisposeOnThrowSource =
        "(module test)\n"
        + CanReadImport
        + @"(define (compute) : Int
  (let ([s (new System.IO.MemoryStream)])
    (with-handlers ([System.Exception e] (if (ms-can-read s) 0 1))
      (use ([m s]) (raise (new System.ArgumentException ""boom""))))))";

    [Fact]
    public void Use_DisposesOnThrow_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndRunInt(DisposeOnThrowSource));
    }

    [Fact]
    public void Use_DisposesOnThrow_Il()
    {
        Assert.Equal(1, CompileIlAndRunInt(DisposeOnThrowSource));
    }

    // use* over two resources created in outer lets; after the use* scope both must
    // be disposed. Returns 1 only when both a and b are disposed.
    private const string UseStarSource =
        "(module test)\n"
        + CanReadImport
        + @"(define (compute) : Int
  (let ([a (new System.IO.MemoryStream)])
    (let ([b (new System.IO.MemoryStream)])
      (begin
        (use* ([x a] [y b]) 0)
        (if (ms-can-read a) 0 (if (ms-can-read b) 0 1))))))";

    [Fact]
    public void UseStar_DisposesAll_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndRunInt(UseStarSource));
    }

    [Fact]
    public void UseStar_DisposesAll_Il()
    {
        Assert.Equal(1, CompileIlAndRunInt(UseStarSource));
    }

    [Fact]
    public void Use_NonDisposableResource_IsHardError()
    {
        // StringBuilder does not implement IDisposable, so the `use` must be rejected.
        var source =
            @"(module test)
(define (compute) : Int
  (use ([sb (new System.Text.StringBuilder)]) 0))";
        var result = CompileWith(source, OutputMode.CSharp);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Diagnostics, d => d.Message.Contains("IDisposable"));
    }

    // ---- Async `use`: the body awaits, so disposal must run via a real try/finally
    // that survives await suspension (IL backend) / native `using` (C# backend).

    // Imports CanRead plus Task.Delay so the body can genuinely suspend.
    private const string AsyncImports =
        "(import-clr\n"
        + "  [ms-can-read System.IO.MemoryStream.CanRead :instance-property : (System.IO.MemoryStream -> Bool)]\n"
        + "  [task-delay System.Threading.Tasks.Task/Delay : (Int -> System.Threading.Tasks.Task)])\n";

    // Body awaits (suspends on Task.Delay), then the resource is disposed once the
    // body completes. Observed via the returned (disposed) resource.
    private const string AsyncDisposeOnCompleteSource =
        "(module test)\n"
        + AsyncImports
        + @"(define-async (compute) : (Task Int)
  (let ([s (new System.IO.MemoryStream)])
    (begin
      (use ([m s]) (await (task-delay 1)))
      (if (ms-can-read s) 0 1))))";

    [Fact]
    public void AsyncUse_DisposesAfterAwait_Il()
    {
        Assert.Equal(1, CompileIlAndAwaitInt(AsyncDisposeOnCompleteSource));
    }

    [Fact]
    public void AsyncUse_DisposesAfterAwait_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndAwaitInt(AsyncDisposeOnCompleteSource));
    }

    // Two awaits inside one async `use` body — both resume points route through the
    // use's trampoline; the resource must survive both suspensions.
    private const string AsyncUseTwoAwaitsSource =
        "(module test)\n"
        + AsyncImports
        + @"(define-async (compute) : (Task Int)
  (let ([s (new System.IO.MemoryStream)])
    (begin
      (use ([m s]) (begin (await (task-delay 1)) (await (task-delay 1))))
      (if (ms-can-read s) 0 1))))";

    [Fact]
    public void AsyncUse_TwoAwaits_Il()
    {
        Assert.Equal(1, CompileIlAndAwaitInt(AsyncUseTwoAwaitsSource));
    }

    [Fact]
    public void AsyncUse_TwoAwaits_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndAwaitInt(AsyncUseTwoAwaitsSource));
    }

    // Body awaits then throws: the finally must dispose on the exception unwind, and
    // the ORIGINAL exception type must still propagate (the outer handler catches the
    // specific ArgumentException and observes the resource disposed).
    private const string AsyncDisposeOnThrowSource =
        "(module test)\n"
        + AsyncImports
        + @"(define-async (compute) : (Task Int)
  (let ([s (new System.IO.MemoryStream)])
    (with-handlers ([System.ArgumentException e] (if (ms-can-read s) 0 1))
      (use ([m s])
        (begin (await (task-delay 1)) (raise (new System.ArgumentException ""boom"")))))))";

    [Fact]
    public void AsyncUse_DisposesOnThrow_Il()
    {
        Assert.Equal(1, CompileIlAndAwaitInt(AsyncDisposeOnThrowSource));
    }

    [Fact]
    public void AsyncUse_DisposesOnThrow_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndAwaitInt(AsyncDisposeOnThrowSource));
    }

    // Mixed nesting: an await sits inside a with-handlers inside a use, so the await's
    // enclosing-try chain is [use, with-handlers] — exercises the generalized
    // trampoline routing across both region kinds.
    private const string AsyncUseNestedWithHandlersSource =
        "(module test)\n"
        + AsyncImports
        + @"(define-async (compute) : (Task Int)
  (let ([s (new System.IO.MemoryStream)])
    (begin
      (use ([m s])
        (with-handlers ([System.Exception e] 0)
          (begin (await (task-delay 1)) 0)))
      (if (ms-can-read s) 0 1))))";

    [Fact]
    public void AsyncUse_NestedWithHandlers_Il()
    {
        Assert.Equal(1, CompileIlAndAwaitInt(AsyncUseNestedWithHandlersSource));
    }

    [Fact]
    public void AsyncUse_NestedWithHandlers_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndAwaitInt(AsyncUseNestedWithHandlersSource));
    }

    // Async use*: nested async `use`; both resources disposed in reverse order after
    // the awaiting body completes.
    private const string AsyncUseStarSource =
        "(module test)\n"
        + AsyncImports
        + @"(define-async (compute) : (Task Int)
  (let ([a (new System.IO.MemoryStream)])
    (let ([b (new System.IO.MemoryStream)])
      (begin
        (use* ([x a] [y b]) (await (task-delay 1)))
        (if (ms-can-read a) 0 (if (ms-can-read b) 0 1))))))";

    [Fact]
    public void AsyncUseStar_DisposesAll_Il()
    {
        Assert.Equal(1, CompileIlAndAwaitInt(AsyncUseStarSource));
    }

    [Fact]
    public void AsyncUseStar_DisposesAll_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndAwaitInt(AsyncUseStarSource));
    }

    // A SYNC `use` (no await in its body) inside an async function still uses the plain
    // try/finally path and disposes correctly between await points.
    private const string SyncUseInAsyncSource =
        "(module test)\n"
        + AsyncImports
        + @"(define-async (compute) : (Task Int)
  (begin
    (await (task-delay 1))
    (let ([s (new System.IO.MemoryStream)])
      (begin
        (use ([m s]) 0)
        (if (ms-can-read s) 0 1)))))";

    [Fact]
    public void SyncUseInsideAsyncFunction_Il()
    {
        Assert.Equal(1, CompileIlAndAwaitInt(SyncUseInAsyncSource));
    }

    [Fact]
    public void SyncUseInsideAsyncFunction_CSharp()
    {
        Assert.Equal(1, CompileCSharpAndAwaitInt(SyncUseInAsyncSource));
    }
}
