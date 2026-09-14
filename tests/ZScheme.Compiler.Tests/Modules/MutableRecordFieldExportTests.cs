using System.Reflection;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Modules;

// `(set! record field value)` must type-check when the record is declared in an imported
// module: the inferer's mutable-field registry is populated from the import's
// `ExportedMutableRecordFields`, which only the module-compilation path produces. A
// single-file program never reaches the gap, so these tests write real files and go
// through ModuleSearchPaths, mirroring TransitiveTypeMetadataTests.
//
// The `with` half pins the shared root cause: imported function-typed exports join the
// env's overload set rather than its plain bindings, so the `RecordName/field` accessor
// lookup (`with` uses the same one as the set! receiver form) has to consult both.
public class MutableRecordFieldExportTests
{
    private const string HelperSource = """
        (module helper)
        (export Counter Counter/n)

        (define-record Counter [n : Int #:mutable] [tag : Int])
        """;

    private const string MainSource = """
        (module main)
        (import helper)

        (define (Compute) : Int
          (let ([c (Counter 1 10)])
            (let ([w (with c [tag 20])])
              (begin (set! c n 5) (+ (Counter/n c) (Counter/tag c) (Counter/tag w))))))
        """;

    [Fact]
    public void SetFieldReceiver_OnImportedRecord_Runs_Il()
    {
        Assert.Equal(35, CompileAndRun(OutputMode.Il));
    }

    [Fact]
    public void SetFieldReceiver_OnImportedRecord_Runs_CSharp()
    {
        Assert.Equal(35, CompileAndRun(OutputMode.CSharp));
    }

    private static int CompileAndRun(OutputMode mode)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_mutable_{Guid.NewGuid():N}");
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
            return (int)compute.Invoke(null, null)!;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

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
            "ZSchemeMutableRecordExec",
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
        var dir = Path.GetDirectoryName(typeof(MutableRecordFieldExportTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }
}
