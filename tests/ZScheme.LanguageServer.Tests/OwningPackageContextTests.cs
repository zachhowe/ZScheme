using Xunit;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

/// <summary>
///     A package that does not sit under a <c>packages/</c> directory gets its entire
///     compilation context from its own manifest. Before these paths were wired up the
///     language server resolved nothing for such a package — not its own import prefix, not
///     its <c>:local</c> dependencies, and not the CLR types behind its <c>(ref …)</c>
///     paths — while the command-line build of the very same package succeeded, because
///     only the CLI went through <c>PackageOptionsBuilder</c>.
/// </summary>
public sealed class OwningPackageContextTests
{
    [Fact]
    public void OwnImportPrefix_ResolvesIntraPackageImport()
    {
        using var ws = new StandalonePackageWorkspace(
            "mypkg",
            new Dictionary<string, string>
            {
                ["lib/helper.zs"] = "(module helper)\n(export helped)\n(define (helped) : Int 1)",
                ["main.zs"] =
                    "(module main)\n(import mypkg/lib/helper)\n(define (go) : Int (helped))",
            }
        );

        var state = ws.Open("src/main.zs");

        Assert.DoesNotContain(
            state.Diagnostics.Diagnostics,
            d => d.Message.Contains("Module not found")
        );
    }

    [Fact]
    public void LocalDependency_ResolvesWithoutPackagesDirectory()
    {
        using var ws = new StandalonePackageWorkspace(
            "mypkg",
            new Dictionary<string, string>
            {
                ["main.zs"] = "(module main)\n(import dep/thing)\n(define (go) : Int (thing))",
            },
            manifestExtras: """(dependencies (zscheme [dep :local "../dep"]))"""
        );

        StandalonePackageWorkspace.WriteDependencyPackage(
            Path.Combine(Directory.GetParent(ws.Root)!.FullName, "dep"),
            "dep",
            new Dictionary<string, string>
            {
                ["thing.zs"] = "(module thing)\n(export thing)\n(define (thing) : Int 7)",
            }
        );

        var state = ws.Open("src/main.zs");

        Assert.DoesNotContain(
            state.Diagnostics.Diagnostics,
            d => d.Message.Contains("Module not found")
        );
    }

    [Fact]
    public void ManifestRefPath_ResolvesImportClrType()
    {
        using var ws = new StandalonePackageWorkspace(
            "mypkg",
            new Dictionary<string, string>
            {
                ["main.zs"] = "(module main)\n(import-clr [ping RefProbeMain.Marker/Ping])",
            },
            manifestExtras: """(build (main (backend "il") (ref "refs")))"""
        );
        ws.WriteProbeAssembly("refs", "RefProbeMain");

        var state = ws.Open("src/main.zs");

        Assert.DoesNotContain(
            state.Diagnostics.Diagnostics,
            d => d.Message.Contains("CLR type not found")
        );
    }

    /// <summary>
    ///     The negative control for <see cref="ManifestRefPath_ResolvesImportClrType" />: with
    ///     the assembly present but no <c>(ref …)</c> naming its directory, the type must stay
    ///     unresolved. Otherwise that test would pass on some incidental probe path and prove
    ///     nothing about ref-path handling.
    /// </summary>
    [Fact]
    public void ProbeAssemblyOutsideRefPaths_StaysUnresolved()
    {
        using var ws = new StandalonePackageWorkspace(
            "mypkg",
            new Dictionary<string, string>
            {
                ["main.zs"] = "(module main)\n(import-clr [ping RefProbeAbsent.Marker/Ping])",
            }
        );
        ws.WriteProbeAssembly("refs", "RefProbeAbsent");

        var state = ws.Open("src/main.zs");

        Assert.Contains(
            state.Diagnostics.Diagnostics,
            d => d.Message.Contains("CLR type not found")
        );
    }

    [Fact]
    public void TestRefPath_ResolvesImportClrTypeInTestFile()
    {
        using var ws = new StandalonePackageWorkspace(
            "mypkg",
            new Dictionary<string, string> { ["main.zs"] = "(module main)\n(define (go) : Int 1)" },
            manifestExtras: """(build (main (backend "il")) (test (ref "test-refs")))""",
            testFiles: new Dictionary<string, string>
            {
                ["main-test.zs"] =
                    "(module main-test)\n(import-clr [ping RefProbeTest.Marker/Ping])",
            }
        );
        ws.WriteProbeAssembly("test-refs", "RefProbeTest");

        var state = ws.Open("test/main-test.zs");

        Assert.DoesNotContain(
            state.Diagnostics.Diagnostics,
            d => d.Message.Contains("CLR type not found")
        );
    }
}
