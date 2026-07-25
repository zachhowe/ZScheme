using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Modules;

// A package's import alias ("mypkg") and the module it points at ("mypkg/mypkg") name the same
// file. If both spellings reach the module graph, the file is compiled twice, and every function
// it exports joins the overload set twice — once as "mypkg/mypkg/<name>" and once as
// "mypkg/<name>" — which surfaces as a bogus "Ambiguous overload" at each call site.
//
// The import triangle below is what packages/aspnet/test does: the root file imports the package
// directly *and* imports a helper module that imports the same package.
public class ModuleAliasDuplicationTests
{
    private const string PkgSource = """
        (module mypkg)
        (export Box Box/v make-box)
        (define-record Box [v : Int])
        (define (make-box [n : Int]) : Box (Box n))
        """;

    private const string MidSource = """
        (module mid)
        (import mypkg)
        (export mid-label)
        (define (mid-label) : String "mid")
        """;

    private const string MainSource = """
        (module main)
        (import mypkg)
        (import mid)
        (define (unwrap-box) : Int (Box/v (make-box 42)))
        """;

    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(ModuleAliasDuplicationTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    // The alias is only reachable transitively through `mid`, so this pins the fix at the
    // per-module transitive-import path as well as the top-level one.
    [Fact]
    public void AliasedPackage_ImportedDirectlyAndTransitively_CompilesWithoutAmbiguity()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_alias_{Guid.NewGuid():N}");
        var pkgDir = Path.Combine(dir, "pkgsrc");
        Directory.CreateDirectory(pkgDir);
        try
        {
            File.WriteAllText(Path.Combine(pkgDir, "mypkg.zs"), PkgSource);
            File.WriteAllText(Path.Combine(dir, "mid.zs"), MidSource);
            var mainPath = Path.Combine(dir, "main.zs");
            File.WriteAllText(mainPath, MainSource);

            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.CSharp,
                    SuppressVersionPreamble = true,
                    DisablePrelude = true,
                    PackagePaths = new Dictionary<string, string>
                    {
                        ["stdlib"] = Path.Combine(RepoRoot(), "packages", "stdlib", "src"),
                        ["mypkg"] = pkgDir,
                    },
                    ModuleSearchPaths = [dir],
                    ModuleAliases = new Dictionary<string, string> { ["mypkg"] = "mypkg/mypkg" },
                }
            );

            var result = compilation.Compile(MainSource, mainPath);

            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Sharper than "it compiles": no two compiled modules may name the same source file. This
    // catches the duplicate even if overload resolution later learns to tolerate it.
    [Fact]
    public void AliasedPackage_IsCompiledExactlyOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_alias_{Guid.NewGuid():N}");
        var pkgDir = Path.Combine(dir, "pkgsrc");
        Directory.CreateDirectory(pkgDir);
        try
        {
            File.WriteAllText(Path.Combine(pkgDir, "mypkg.zs"), PkgSource);
            File.WriteAllText(Path.Combine(dir, "mid.zs"), MidSource);
            var mainPath = Path.Combine(dir, "main.zs");
            File.WriteAllText(mainPath, MainSource);

            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.CSharp,
                    SuppressVersionPreamble = true,
                    DisablePrelude = true,
                    PackagePaths = new Dictionary<string, string>
                    {
                        ["stdlib"] = Path.Combine(RepoRoot(), "packages", "stdlib", "src"),
                        ["mypkg"] = pkgDir,
                    },
                    ModuleSearchPaths = [dir],
                    ModuleAliases = new Dictionary<string, string> { ["mypkg"] = "mypkg/mypkg" },
                }
            );

            var result = compilation.Compile(MainSource, mainPath);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));

            // The alias must never be cached as a module of its own alongside its target, and no
            // two cached modules may name the same source file.
            var cached = compilation.GetCachedModules();
            Assert.Contains("mypkg/mypkg", cached.Keys);
            Assert.DoesNotContain("mypkg", cached.Keys);
            Assert.Single(cached.Values, m => m.Name == "mypkg/mypkg");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
