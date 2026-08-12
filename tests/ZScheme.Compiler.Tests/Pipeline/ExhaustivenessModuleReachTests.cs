using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Pipeline;

/// <summary>
///     ZS0002 must see unions a module reaches <em>transitively</em>, not just the ones it names in
///     its own <c>(import ...)</c> forms.
///
///     <see cref="Types.ExhaustivenessValidator" /> was handed the module's direct imports only, so
///     a union declared by a dependency's dependency never reached the checker — and an
///     unregistered union has no case list to compare the arms against, making the checker
///     permissive rather than noisy. That made the same non-exhaustive match a hard error in a
///     primary compilation unit and silently accepted the moment it moved into a library module,
///     which is the shape every package build takes.
///
///     The everyday case is <c>Option</c>: matching on the result of an imported function does not
///     require importing <c>stdlib/option</c>, so the union backing the pattern is almost always
///     one import further out than the checker could see.
///
///     Same family as <see cref="TailRecursionAnalyzerModuleReachTests" /> — a front-end analysis
///     wired into <see cref="Compilation.Compile" /> with a narrower counterpart on the module
///     path — and the same fix: give the module path the closure the whole-program path uses.
/// </summary>
public class ExhaustivenessModuleReachTests
{
    /// <summary>Declares the union. Nothing downstream imports this module by name.</summary>
    private const string BaseSource = """
        (module base)
        (define-union (Box ^a) (Full [value : ^a]) (Empty))
        (export Box Full Empty)
        """;

    /// <summary>The only module that imports <c>base</c>; it hands <c>Box</c> back as a value.</summary>
    private const string MidSource = """
        (module mid)
        (import test-pkg/base)
        (define (wrap [n : Int]) : (Box Int) (Full n))
        (export wrap)
        """;

    [Fact]
    public void ModuleMatchingATransitivelyImportedUnion_ReportsTheMissingCase()
    {
        WithPackage(
            [
                ("base.zs", BaseSource),
                ("mid.zs", MidSource),
                (
                    "user.zs",
                    """
                    (module user)
                    (import test-pkg/mid)
                    (define (peek [n : Int]) : Int (match (wrap n) [(Full v) v]))
                    (export peek)
                    """
                ),
            ],
            diag =>
            {
                var error = Assert.Single(NonExhaustive(diag));
                Assert.Equal("Empty/0", error.Data![0]);
                Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            }
        );
    }

    /// <summary>
    ///     The other half: covering every case must stay quiet. Widening the union set can only add
    ///     diagnostics, so without this the fix could just as well have been noise.
    /// </summary>
    [Fact]
    public void ModuleCoveringEveryCaseOfATransitivelyImportedUnion_IsSilent()
    {
        WithPackage(
            [
                ("base.zs", BaseSource),
                ("mid.zs", MidSource),
                (
                    "user.zs",
                    """
                    (module user)
                    (import test-pkg/mid)
                    (define (peek [n : Int]) : Int (match (wrap n) [(Full v) v] [(Empty) 0]))
                    (export peek)
                    """
                ),
            ],
            diag => Assert.Empty(NonExhaustive(diag))
        );
    }

    /// <summary>
    ///     <c>Option</c> reached through <c>stdlib/mutable/hash</c> — the shape that motivated this,
    ///     and the one every package in the repo actually writes.
    /// </summary>
    [Fact]
    public void ModuleMatchingStdlibOptionWithoutImportingIt_ReportsTheMissingCase()
    {
        WithPackage(
            [
                (
                    "user.zs",
                    """
                    (module user)
                    (import stdlib/mutable/hash)
                    (define (peek [h : (Mutable-Hash String Int)]) : Int
                      (match (hash-ref h "a") [(Some v) v]))
                    (export peek)
                    """
                ),
            ],
            diag =>
            {
                var error = Assert.Single(NonExhaustive(diag));
                Assert.Equal("None/0", error.Data![0]);
            }
        );
    }

    #region Helpers

    private static IEnumerable<Diagnostic> NonExhaustive(DiagnosticBag diag) =>
        diag.Diagnostics.Where(d => d.Code == DiagnosticCodes.NonExhaustiveMatch);

    /// <summary>
    ///     Compiles <paramref name="files" /> as a package library and hands the build's
    ///     diagnostics to <paramref name="assert" />. Goes through <see cref="LibraryCompiler" />
    ///     rather than calling the validator directly: the gap being pinned is which union set the
    ///     module path passes in, so a test that constructed that set itself would have passed
    ///     throughout.
    /// </summary>
    private static void WithPackage(
        (string Name, string Source)[] files,
        Action<DiagnosticBag> assert
    )
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_zs0002_reach_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, source) in files)
                File.WriteAllText(Path.Combine(dir, name), source);

            var diag = new DiagnosticBag();
            new LibraryCompiler(diag).CompileToCSharp(
                dir,
                MakeManifest(),
                new CompilerOptions
                {
                    OutputMode = OutputMode.CSharp,
                    PackagePaths = new Dictionary<string, string>
                    {
                        ["stdlib"] = GetStdLibPath(),
                        ["test-pkg"] = dir,
                    },
                }
            );

            // Any error that is not the one under test means the sources stopped compiling for an
            // unrelated reason — which would otherwise read as "no missing case reported".
            Assert.DoesNotContain(
                diag.Diagnostics,
                d =>
                    d.Severity == DiagnosticSeverity.Error
                    && d.Code != DiagnosticCodes.NonExhaustiveMatch
            );
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
        var dir = Path.GetDirectoryName(typeof(ExhaustivenessModuleReachTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    #endregion
}
