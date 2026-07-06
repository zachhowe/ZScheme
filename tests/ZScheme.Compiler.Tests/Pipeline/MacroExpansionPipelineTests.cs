using Xunit;
using ZScheme.Compiler.Pipeline;
using ZScheme.Compiler.Syntax;

namespace ZScheme.Compiler.Tests.Pipeline;

public class MacroExpansionPipelineTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(MacroExpansionPipelineTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    [Fact]
    public void StopAfterMacroExpansion_ReturnsResultAndExposesSExprs()
    {
        var trace = new MacroExpansionTrace();
        var compilation = new Compilation(
            new CompilerOptions
            {
                AllowsImplicitModuleName = true,
                StopAfterMacroExpansion = true,
                MacroObserver = trace,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );

        var result = compilation.Compile(
            @"(module test)
            (define-syntax my-if
              (syntax-rules ()
                [(my-if c t e) (if c t e)]))
            (define (f) (my-if #t 1 2))"
        );

        Assert.IsType<CompilationResult.MacroExpansionResult>(result);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        Assert.NotNull(compilation.RawSExprs);
        Assert.NotNull(compilation.ExpandedSExprs);
        Assert.Contains(
            compilation.RawSExprs,
            s => s.ToString().Contains("(my-if #t 1 2)")
        );
        Assert.Contains(
            compilation.ExpandedSExprs,
            s => s.ToString() == "(define (f) (if #t 1 2))"
        );

        var step = Assert.Single(trace.Steps);
        Assert.Equal("my-if", step.Macro.Name);
    }

    [Fact]
    public void ImportedModuleInternalExpansion_IsNotTraced()
    {
        var moduleDir = Path.Combine(Path.GetTempPath(), $"zs_macrotrace_{Guid.NewGuid():N}");
        Directory.CreateDirectory(moduleDir);
        try
        {
            // The helper module defines a macro, uses it internally, and exports it. Its
            // internal expansion runs in CompileModule with a separate expander and must not
            // reach the main compilation's observer.
            File.WriteAllText(
                Path.Combine(moduleDir, "helper.zs"),
                @"(module helper)
                (export my-twice helper-val)
                (define-syntax my-twice
                  (syntax-rules ()
                    [(my-twice x) (+ x x)]))
                (define (helper-val) : Int (my-twice 3))"
            );

            var trace = new MacroExpansionTrace();
            var compilation = new Compilation(
                new CompilerOptions
                {
                    AllowsImplicitModuleName = true,
                    StopAfterMacroExpansion = true,
                    MacroObserver = trace,
                    PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
                    ModuleSearchPaths = [moduleDir],
                }
            );

            var result = compilation.Compile(
                @"(module test)
                (import helper)
                (define (g) : Int (my-twice 7))"
            );

            Assert.True(
                result.Success,
                "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
            );

            // Only the main file's single use is traced, not helper.zs's internal one
            var step = Assert.Single(trace.Steps);
            Assert.Equal("my-twice", step.Macro.Name);
            Assert.Equal("(define (g) : Int (my-twice 7))", step.FormBefore.ToString());
        }
        finally
        {
            Directory.Delete(moduleDir, true);
        }
    }

    [Fact]
    public void ExpansionFailure_StillExposesPartialExpansion()
    {
        var trace = new MacroExpansionTrace();
        var compilation = new Compilation(
            new CompilerOptions
            {
                AllowsImplicitModuleName = true,
                StopAfterMacroExpansion = true,
                MacroObserver = trace,
                DisablePrelude = true,
            }
        );

        var result = compilation.Compile(
            @"(define-syntax loop
              (syntax-rules ()
                [(loop) (loop)]))
            (loop)"
        );

        Assert.IsType<CompilationResult.MacroExpanderFailure>(result);
        Assert.NotNull(compilation.ExpandedSExprs);
        Assert.True(trace.DepthLimitHit);
        Assert.True(trace.Steps.Count >= 100);
    }
}
