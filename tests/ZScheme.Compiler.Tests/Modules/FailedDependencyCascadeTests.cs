using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Modules;

public class FailedDependencyCascadeTests
{
    private static string CreateTempDir()
    {
        return Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
    }

    [Fact]
    public void DependencyTypeError_DoesNotProduceFalseCircularDiagnostic()
    {
        // Regression: when a transitively-imported module fails type inference, every
        // downstream importer used to report "Circular module dependency involving 'X'"
        // because the failed module was never removed from _compilingModules. The real
        // underlying error should be reported exactly once and no false-cycle diagnostic
        // should appear.
        var dir = CreateTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            // broken.zs: Some branch returns ^a instead of (Option ^a) -> occurs-check failure.
            File.WriteAllText(
                Path.Combine(dir, "broken.zs"),
                @"
(module broken)
(define-union (Box ^a) (Wrap [value : ^a]))
(define (rewrap [b : (Box ^a)]) : (Box ^a)
  (match b
    [(Wrap v) v]))
(export Box Wrap rewrap)"
            );

            File.WriteAllText(
                Path.Combine(dir, "b.zs"),
                @"
(module b)
(import broken)
(define (use-b [x : Int]) : Int x)
(export use-b)"
            );

            File.WriteAllText(
                Path.Combine(dir, "c.zs"),
                @"
(module c)
(import broken)
(define (use-c [x : Int]) : Int x)
(export use-c)"
            );

            var mainSource =
                @"
(module main)
(import b)
(import c)
(define (main) : Int (+ (use-b 1) (use-c 2)))";
            var mainPath = Path.Combine(dir, "main.zs");
            File.WriteAllText(mainPath, mainSource);

            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.CSharp,
                    AllowsImplicitModuleName = true,
                }
            );
            var result = compilation.Compile(mainSource, mainPath);

            Assert.False(
                result.Success,
                "Compilation should fail because broken.zs has a type error"
            );

            var messages = result.Diagnostics.Diagnostics.Select(d => d.Message).ToList();

            Assert.DoesNotContain(messages, m => m.Contains("Circular module dependency"));

            var realErrors = messages.Where(m => m.Contains("Infinite type")).ToList();
            Assert.Single(realErrors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
