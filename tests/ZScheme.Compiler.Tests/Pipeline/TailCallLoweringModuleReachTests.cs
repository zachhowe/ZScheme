using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Pipeline;

/// <summary>
///     Tail-call lowering must reach functions that arrive at an emitter as an <em>imported
///     module</em>, not just the ones in the main IR.
///
///     Both emitters used to run <see cref="Ir.TailCallLowering" /> over their <c>node</c>
///     argument alone. That covered a program's own functions and nothing else, so an inlined
///     source module never looped on the C# backend, and a package library — which emits with an
///     empty main IR and hands every one of its functions over as an imported module — never
///     looped on <em>either</em> backend. The whole stdlib compiled to stack-consuming recursion
///     with no diagnostic, because <see cref="Types.TailRecursionAnalyzer" /> is correctly silent
///     on a tail self-call: it models what the pass would do, and the pass was not running.
///
///     Each backend is checked structurally first (a clean failure) and then by running a
///     million-deep loop, which is what actually proves constant stack — and which, on
///     regression, takes the test host down with a StackOverflow rather than failing. The
///     structural assertion ahead of it is what keeps that from being the first thing you see.
/// </summary>
public class TailCallLoweringModuleReachTests
{
    /// <summary>
    ///     A module whose only function is the accumulator loop every stdlib <c>*-loop</c>
    ///     helper is shaped like. Deliberately the <em>only</em> recursive function in play:
    ///     the emitted C# is then loop-free unless the module itself was lowered.
    /// </summary>
    private const string DepModuleSource = """
        (module dep)
        (define (dep-loop [n : Int] [acc : Int]) : Int
          (if (= n 0) acc (dep-loop (- n 1) (+ acc 1))))
        (export dep-loop)
        """;

    /// <summary>Main IR with no recursion of its own, so it contributes no loop.</summary>
    private const string MainSource = """
        (module main)
        (import dep)
        (define (Compute) : Int (dep-loop 1000000 0))
        """;

    #region Inlined source module (program path)

    [Fact]
    public void InlinedSourceModule_LoopsOnTheCSharpBackend()
    {
        WithTempDir(dir =>
        {
            var cs = CompileProgram(dir, OutputMode.CSharp)
                is CompilationResult.CSharpOutputResult r
                ? r.CsOutput
                : throw new InvalidOperationException("expected C# output");

            Assert.Contains("while (true)", cs);
            Assert.Equal(1000000, RunCSharp(cs));
        });
    }

    [Fact]
    public void InlinedSourceModule_LoopsOnTheIlBackend()
    {
        WithTempDir(dir =>
        {
            var bytes = (
                (CompilationResult.IlOutputResult)CompileProgram(dir, OutputMode.Il)
            ).OutputBytes;

            AssertDoesNotCallItself(bytes, "Loop");
            Assert.Equal(1000000, RunIlZeroArg(bytes, "Compute"));
        });
    }

    private static CompilationResult CompileProgram(string dir, OutputMode mode)
    {
        File.WriteAllText(Path.Combine(dir, "dep.zs"), DepModuleSource);
        var mainPath = Path.Combine(dir, "main.zs");
        File.WriteAllText(mainPath, MainSource);

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = mode,
                AllowsImplicitModuleName = true,
                DisablePrelude = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        var result = compilation.Compile(MainSource, mainPath);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return result;
    }

    #endregion

    #region Package library (LibraryCompiler path)

    [Fact]
    public void PackageLibrary_LoopsOnTheCSharpBackend()
    {
        WithTempDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "loops.zs"), DepModuleSource);

            var diag = new DiagnosticBag();
            var result = new LibraryCompiler(diag).CompileToCSharp(
                dir,
                MakeManifest(),
                MakeOptions(OutputMode.CSharp)
            );

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            // The library's main IR is an empty Seq, so this loop can only come from the
            // module definitions — exactly the ones the emitter used to skip.
            Assert.Contains("while (true)", result.CsOutput);
        });
    }

    [Fact]
    public void PackageLibrary_LoopsOnTheIlBackend()
    {
        WithTempDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "loops.zs"), DepModuleSource);

            var diag = new DiagnosticBag();
            var result = new LibraryCompiler(diag).Compile(
                dir,
                MakeManifest(),
                MakeOptions(OutputMode.Il)
            );

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            AssertDoesNotCallItself(result.AssemblyBytes, "Loop");

            var method = FindMethod(Assembly.Load(result.AssemblyBytes), "Loop");
            Assert.Equal(1000000, method.Invoke(null, [1000000, 0]));
        });
    }

    private static PackageManifest MakeManifest() =>
        new(
            "test-pkg",
            "0.1.0",
            null,
            null,
            null,
            null,
            null,
            new PackageDependencies([], []),
            new PackageDependencies([], []),
            new BuildConfig(new MainBuildConfig(null, null, null, []), null),
            null,
            SourceSpan.None
        );

    private static CompilerOptions MakeOptions(OutputMode mode) =>
        new()
        {
            OutputMode = mode,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
        };

    #endregion

    #region Helpers

    /// <summary>
    ///     Asserts the emitted method no longer contains a <c>call</c> to itself — i.e. its
    ///     self-recursion became a back-edge. The IL backend has no source text to grep, and a
    ///     "does it branch backwards" check would also fire on ordinary loops the emitter
    ///     produces; a self-call is the precise thing tail-call lowering removes.
    /// </summary>
    private static void AssertDoesNotCallItself(byte[] assemblyBytes, string nameFragment)
    {
        var module = ModuleDefinition.FromBytes(assemblyBytes);
        var method = module
            .GetAllTypes()
            .SelectMany(t => t.Methods)
            .Single(m => m.Name?.Value.Contains(nameFragment, StringComparison.Ordinal) == true);

        var selfCalls = method
            .CilMethodBody!.Instructions.Where(i =>
                i.OpCode.Code is CilCode.Call or CilCode.Callvirt
                && ReferenceEquals(i.Operand, method)
            )
            .ToList();

        Assert.True(
            selfCalls.Count == 0,
            $"'{method.Name}' still calls itself {selfCalls.Count} time(s): it was not lowered to a loop."
        );
    }

    private static MethodInfo FindMethod(Assembly asm, string nameFragment) =>
        asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Single(m => m.Name.Contains(nameFragment, StringComparison.Ordinal));

    private static int RunIlZeroArg(byte[] assemblyBytes, string methodName)
    {
        var method = Assembly
            .Load(assemblyBytes)
            .GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        return (int)method.Invoke(null, null)!;
    }

    private static int RunCSharp(string csSource, string methodName = "Compute")
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

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "ZSchemeTcoReach",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(csSource)],
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                // Debug, so the JIT cannot turn the recursion into a loop on its own and
                // hide a regression behind its own tail-call optimization.
                optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Debug,
                allowUnsafe: true,
                nullableContextOptions: Microsoft.CodeAnalysis.NullableContextOptions.Enable
            )
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

        return RunIlZeroArg(ms.ToArray(), methodName);
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(
            typeof(TailCallLoweringModuleReachTests).Assembly.Location
        )!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static void WithTempDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_tco_reach_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            body(dir);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion
}
