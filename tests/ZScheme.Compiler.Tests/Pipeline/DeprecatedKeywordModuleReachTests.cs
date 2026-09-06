using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Pipeline;

/// <summary>
///     ZS0007 must reach source that arrives at the compiler as a <em>package module</em>, not
///     just a single file's main program.
///
///     The module path builds its AST into a <c>DiagnosticBag</c> it copies out only when that
///     bag has errors, so a warning raised there during a module that compiles cleanly is
///     dropped on the floor. A package full of deprecated heads built silently while the same
///     source in one file warned — which would have hidden the deprecation from exactly the
///     code that has to migrate. The builder therefore reports ZS0007 into the compilation's
///     own bag, the way <c>TailRecursionAnalyzer</c> reports ZS0005.
///
///     This is the ZS0007 twin of <see cref="TailRecursionAnalyzerModuleReachTests" />.
/// </summary>
public class DeprecatedKeywordModuleReachTests
{
    private const string LegacySource = """
        (module legacy)
        (define-record Point [x : Int] [y : Int])
        (export Point)
        """;

    private const string ModernSource = """
        (module modern)
        (record Point [x : Int] [y : Int])
        (provide Point)
        """;

    [Fact]
    public void PackageModule_WarnsOnEachDeprecatedHead()
    {
        WithPackage(
            [("legacy.zs", LegacySource)],
            diag =>
            {
                var warnings = Deprecated(diag).ToArray();
                Assert.Equal(2, warnings.Length);
                Assert.Equal(["define-record", "record"], warnings[0].Data);
                Assert.Equal(["export", "provide"], warnings[1].Data);
                Assert.All(warnings, w => Assert.Equal(DiagnosticSeverity.Warning, w.Severity));
            }
        );
    }

    [Fact]
    public void PackageModule_IsSilentOnTheModernHeads()
    {
        WithPackage([("modern.zs", ModernSource)], diag => Assert.Empty(Deprecated(diag)));
    }

    /// <summary>
    ///     The sub-compilation each module gets is built from a fresh
    ///     <see cref="CompilerOptions" />, so the manifest's <c>(warn-deprecated-keyword "false")</c>
    ///     only means anything if <see cref="LibraryCompiler" /> carries the flag across.
    /// </summary>
    [Fact]
    public void PackageModule_HonoursTheWarnDeprecatedKeywordOptOut()
    {
        WithPackage(
            [("legacy.zs", LegacySource)],
            diag => Assert.Empty(Deprecated(diag)),
            warnDeprecatedKeyword: false
        );
    }

    /// <summary>
    ///     A module several siblings import is compiled once and must be warned about once —
    ///     otherwise the noise scales with the dependency graph's fan-in.
    /// </summary>
    [Fact]
    public void ModuleImportedBySiblings_IsReportedOnce()
    {
        WithPackage(
            [
                ("legacy.zs", LegacySource),
                (
                    "first.zs",
                    """
                    (module first)
                    (import test-pkg/legacy)
                    (define (first-use) : Int (Point-x (Point 1 2)))
                    (provide first-use)
                    """
                ),
                (
                    "second.zs",
                    """
                    (module second)
                    (import test-pkg/legacy)
                    (define (second-use) : Int (Point-y (Point 1 2)))
                    (provide second-use)
                    """
                ),
            ],
            diag => Assert.Equal(2, Deprecated(diag).Count())
        );
    }

    #region Helpers

    private static IEnumerable<Diagnostic> Deprecated(DiagnosticBag diag) =>
        diag.Diagnostics.Where(d => d.Code == DiagnosticCodes.DeprecatedKeyword);

    /// <summary>
    ///     Compiles <paramref name="files" /> as a package library and hands the build's
    ///     diagnostics to <paramref name="assert" />. Goes through <see cref="LibraryCompiler" />
    ///     rather than the AST builder directly: the gap being pinned is the wiring, so a test
    ///     that built the AST itself would have passed throughout.
    /// </summary>
    private static void WithPackage(
        (string Name, string Source)[] files,
        Action<DiagnosticBag> assert,
        bool warnDeprecatedKeyword = true
    )
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_zs0007_reach_{Guid.NewGuid():N}");
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
                    WarnDeprecatedKeyword = warnDeprecatedKeyword,
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
            typeof(DeprecatedKeywordModuleReachTests).Assembly.Location
        )!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    #endregion
}
