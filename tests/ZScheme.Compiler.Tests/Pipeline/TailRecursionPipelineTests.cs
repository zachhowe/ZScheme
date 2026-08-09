using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Pipeline;

/// <summary>
///     ZS0005 as it reaches consumers: through the real pipeline (so the language server's
///     stop-after-inference path sees it), honouring the opt-out, and — critically — not
///     leaking out of imported modules into the importer's diagnostics.
/// </summary>
public class TailRecursionPipelineTests
{
    private static DiagnosticBag Compile(string source, bool warnUnloopedRecursion = true)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                AllowsImplicitModuleName = true,
                StopAfterTypeInference = true,
                WarnUnloopedRecursion = warnUnloopedRecursion,
            }
        );
        compilation.Compile(source, "test.zs");
        return compilation.GetDiagnostics();
    }

    private static IEnumerable<Diagnostic> Unlooped(DiagnosticBag diag)
    {
        return diag.Diagnostics.Where(d => d.Code == DiagnosticCodes.NonLoopedSelfRecursion);
    }

    [Fact]
    public void NonTailRecursion_ReachesTheBag_BeforeIrLowering()
    {
        var diag = Compile("(define (fact [n : Int]) : Int (if (= n 0) 1 (* n (fact (- n 1)))))");

        var warning = Assert.Single(Unlooped(diag));
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(["fact", "not-tail"], warning.Data);
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void TailRecursion_IsSilent()
    {
        var diag = Compile(
            "(define (loop [n : Int] [acc : Int]) : Int (if (= n 0) acc (loop (- n 1) (+ acc n))))"
        );

        Assert.Empty(Unlooped(diag));
    }

    [Fact]
    public void RecursiveMarker_Silences_ThroughTheWholePipeline()
    {
        var diag = Compile(
            "(define #:recursive (fact [n : Int]) : Int (if (= n 0) 1 (* n (fact (- n 1)))))"
        );

        Assert.Empty(Unlooped(diag));
        Assert.False(diag.HasErrors);
    }

    [Fact]
    public void DisabledOption_Silences()
    {
        var diag = Compile(
            "(define (fact [n : Int]) : Int (if (= n 0) 1 (* n (fact (- n 1)))))",
            warnUnloopedRecursion: false
        );

        Assert.Empty(Unlooped(diag));
    }

    [Fact]
    public void ImportedModules_DoNotLeakTheirWarnings()
    {
        // Dependency modules run only ExhaustivenessValidator and merge their bag on failure
        // paths only. That containment is what keeps a consumer's build quiet about recursion
        // in the stdlib, so pin it here.
        var dir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "dep.zs"),
                """
                (module dep)
                (define (dep/fact [n : Int]) : Int (if (= n 0) 1 (* n (dep/fact (- n 1)))))
                (export dep/fact)
                """
            );

            const string mainSource = """
                (module main)
                (import dep)
                (define (main) : Int (dep/fact 5))
                """;
            var mainPath = Path.Combine(dir, "main.zs");
            File.WriteAllText(mainPath, mainSource);

            var compilation = new Compilation(
                new CompilerOptions
                {
                    AllowsImplicitModuleName = true,
                    StopAfterTypeInference = true,
                }
            );
            var result = compilation.Compile(mainSource, mainPath);

            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));
            Assert.Empty(Unlooped(compilation.GetDiagnostics()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
