using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Modules;

// A module's pattern metadata has to cover its *transitive* dependencies, not just the modules it
// imports by name. `helper` below imports only `stdlib/mutable/hash`, but `hash-ref` hands back an
// `Option`, whose declaration lives in `stdlib/option` — hash's own dependency. When the union
// registry was populated from direct imports alone, PatternResolver could not resolve `(Some b)`
// and annotated its binder with a null field type. Nothing failed loudly: the async state-machine
// analyzer treats a null field type as "do not hoist", so `b` got no state-machine field, was
// never saved across the suspension, and read back as null on resume.
//
// The equivalent single-file program in Integration/EndToEndTests.cs passed throughout, because
// the whole-program path always registered the full module closure. Only compiling *as a module*
// reaches the gap, which is why these tests write real files and go through ModuleSearchPaths.
public class TransitiveTypeMetadataTests
{
    // The awaited task must genuinely suspend: `await` on a completed Task never yields, so
    // MoveNext runs straight through and the binder survives in its CIL local even with the bug.
    private const string HelperSource = """
        (module helper)
        (import stdlib/mutable/hash)
        (import-clr
          [task-delay System.Threading.Tasks.Task/Delay : (Int -> System.Threading.Tasks.Task)])
        (export Box Box/n open-first)

        (define-record Box [n : Int])

        (define-async (open-first [h : (Mutable-Hash String Box)]) : (Task Int)
          (match (hash-ref h "a")
            [(Some b) (begin (await (task-delay 1)) (Box/n b))]
            [None 0]))
        """;

    private const string MainSource = """
        (module main)
        (import helper)
        (import stdlib/mutable/hash)

        (define-async (Compute) : (Task Int)
          (let ([h (make-hash)])
            (begin
              (hash-set! h "a" (Box 42))
              (await (open-first h)))))
        """;

    [Fact]
    public void MatchArmBinderOverATransitiveUnion_SurvivesASuspension_Il()
    {
        Assert.Equal(42, CompileModulesAndAwaitInt(OutputMode.Il));
    }

    // The C# backend lowers an awaiting arm into a lambda that captures the binder, so it was
    // right even with the registry gap. Kept as the differential half: the two backends have to
    // agree here, and this is the side that pins which one moved if they stop agreeing.
    [Fact]
    public void MatchArmBinderOverATransitiveUnion_SurvivesASuspension_CSharp()
    {
        Assert.Equal(42, CompileModulesAndAwaitInt(OutputMode.CSharp));
    }

    // Writes the two-module fixture to a temp directory, compiles `main` through the requested
    // backend, and awaits its zero-arg `Compute`. The bug produced a wrong runtime value (a null
    // binder, so an NRE), never a diagnostic, so the assertion has to be on the executed result.
    private static int CompileModulesAndAwaitInt(OutputMode mode)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_transitive_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "helper.zs"), HelperSource);
            var mainPath = Path.Combine(dir, "main.zs");
            File.WriteAllText(mainPath, MainSource);

            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = mode,
                    ModuleSearchPaths = [dir],
                    PackagePaths = new Dictionary<string, string> { ["stdlib"] = StdLibPath() },
                }
            );
            var result = compilation.Compile(MainSource, mainPath);
            Assert.True(
                result.Success,
                "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
            );

            var asm = mode == OutputMode.Il
                ? Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes)
                : RoslynCompile(((CompilationResult.CSharpOutputResult)result).CsOutput);

            var compute = asm.GetExportedTypes()
                .SelectMany(t => t.GetMethods())
                .First(m =>
                    m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                    && m.GetParameters().Length == 0
                );
            var task = (Task<int>)compute.Invoke(null, null)!;
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Builds the transpiled C# into an in-memory assembly, mirroring
    // Integration/EndToEndTests.CompileCSharpToMethod.
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

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "ZSchemeTransitiveModuleExec",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(cs)],
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release,
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
        return Assembly.Load(ms.ToArray());
    }

    private static string StdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(TransitiveTypeMetadataTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }
}
