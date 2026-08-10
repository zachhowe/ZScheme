using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Pipeline;

/// <summary>
///     ZS0005 must reach functions that arrive at the compiler as a <em>package module</em>, not
///     just the ones in a single file's main program.
///
///     <see cref="Types.TailRecursionAnalyzer" /> ran as stage 4.8 of
///     <see cref="Compilation.Compile" /> and nowhere else, so a package build — which routes
///     every one of its modules through <c>CompileAsModule</c> — analysed nothing at all. A
///     package whose source contained an obviously un-loopable self-recursion built silently,
///     while the same function in a single file warned. That made the analyzer blind to exactly
///     the code that most needs it: library code, run at unknown depth by unknown callers.
///
///     This is the diagnostic twin of <see cref="TailCallLoweringModuleReachTests" />, which pins
///     the <em>pass</em> to the module paths; without both, the package path can silently diverge
///     from the main path on TCO in either direction. <c>TailRecursionDriftTests</c> pins
///     "analyzer silence ⇔ <c>IsTcoLoop</c>", but only through <see cref="Compilation.Compile" />,
///     so it cannot see this gap.
/// </summary>
public class TailRecursionAnalyzerModuleReachTests
{
    /// <summary>The recursive call sits under a multiply, so it can never be a back-edge.</summary>
    private const string UnloopableSource = """
        (module unloopable)
        (define (fact [n : Int]) : Int (if (= n 0) 1 (* n (fact (- n 1)))))
        (export fact)
        """;

    /// <summary>The same shape written as an accumulator, which does become a loop.</summary>
    private const string LoopableSource = """
        (module loopable)
        (define (count [n : Int] [acc : Int]) : Int (if (= n 0) acc (count (- n 1) (+ acc 1))))
        (export count)
        """;

    [Fact]
    public void PackageModule_WarnsOnUnloopedSelfRecursion()
    {
        WithPackage(
            [("unloopable.zs", UnloopableSource)],
            diag =>
            {
                var warning = Assert.Single(Unlooped(diag));
                Assert.Equal("fact", warning.Data![0]);
                Assert.Equal("not-tail", warning.Data![1]);
                Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
            }
        );
    }

    /// <summary>
    ///     The other half of the drift biconditional: a module function the pass <em>does</em>
    ///     loop must stay quiet, or wiring the analyzer in would just trade silence for noise.
    /// </summary>
    [Fact]
    public void PackageModule_IsSilentWhenTheSelfCallBecomesALoop()
    {
        WithPackage([("loopable.zs", LoopableSource)], diag => Assert.Empty(Unlooped(diag)));
    }

    /// <summary>
    ///     The sub-compilation each module gets is built from a fresh
    ///     <see cref="CompilerOptions" />, so the manifest's <c>(warn-unlooped-recursion "false")</c>
    ///     only means anything if <see cref="LibraryCompiler" /> carries the flag across.
    /// </summary>
    [Fact]
    public void PackageModule_HonoursTheWarnUnloopedRecursionOptOut()
    {
        WithPackage(
            [("unloopable.zs", UnloopableSource)],
            diag => Assert.Empty(Unlooped(diag)),
            warnUnloopedRecursion: false
        );
    }

    /// <summary>
    ///     A module that several siblings import is compiled once and must be warned about once.
    ///     Reporting per-importer would scale the noise with the dependency graph's fan-in.
    /// </summary>
    [Fact]
    public void ModuleImportedBySiblings_IsReportedOnce()
    {
        WithPackage(
            [
                ("unloopable.zs", UnloopableSource),
                (
                    "first.zs",
                    """
                    (module first)
                    (import test-pkg/unloopable)
                    (define (first-use [n : Int]) : Int (fact n))
                    (export first-use)
                    """
                ),
                (
                    "second.zs",
                    """
                    (module second)
                    (import test-pkg/unloopable)
                    (define (second-use [n : Int]) : Int (fact n))
                    (export second-use)
                    """
                ),
            ],
            diag => Assert.Single(Unlooped(diag))
        );
    }

    #region Helpers

    private static IEnumerable<Diagnostic> Unlooped(DiagnosticBag diag) =>
        diag.Diagnostics.Where(d => d.Code == DiagnosticCodes.NonLoopedSelfRecursion);

    /// <summary>
    ///     Compiles <paramref name="files" /> as a package library and hands the build's
    ///     diagnostics to <paramref name="assert" />. Goes through <see cref="LibraryCompiler" />
    ///     rather than calling the analyzer directly: the gap being pinned is the wiring, so a
    ///     test that invoked the analyzer itself would have passed throughout.
    /// </summary>
    private static void WithPackage(
        (string Name, string Source)[] files,
        Action<DiagnosticBag> assert,
        bool warnUnloopedRecursion = true
    )
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_zs0005_reach_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, source) in files)
                File.WriteAllText(Path.Combine(dir, name), source);

            var diag = new DiagnosticBag();
            var result = new LibraryCompiler(diag).CompileToCSharp(
                dir,
                MakeManifest(),
                new CompilerOptions
                {
                    OutputMode = OutputMode.CSharp,
                    WarnUnloopedRecursion = warnUnloopedRecursion,
                    PackagePaths = new Dictionary<string, string>
                    {
                        ["stdlib"] = GetStdLibPath(),
                        ["test-pkg"] = dir,
                    },
                }
            );

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            assert(diag);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
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

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(
            typeof(TailRecursionAnalyzerModuleReachTests).Assembly.Location
        )!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    #endregion
}
