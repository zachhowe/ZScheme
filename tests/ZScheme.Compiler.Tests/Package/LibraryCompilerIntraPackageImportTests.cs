using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Package;

// Under an import-prefix a package's own modules are keyed by their prefixed names
// ("mypkg/helper"), but a sibling import may be spelled bare ("helper") — and for the default
// module, bare is the spelling consumers use. Both spellings name one file. If the bare form
// never reaches the dependency graph, the sub-compilation's search-path fallback finds that same
// file and compiles it again under the bare name, so every definition it exports lands in the
// package assembly twice.
public class LibraryCompilerIntraPackageImportTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(
            typeof(LibraryCompilerIntraPackageImportTests).Assembly.Location
        )!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_intrapkg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PackageManifest MakeManifest(string importPrefix, string? defaultModule = null)
    {
        return new PackageManifest(
            "test-pkg",
            "0.1.0",
            null,
            importPrefix,
            defaultModule,
            null,
            null,
            new PackageDependencies([], []),
            new PackageDependencies([], []),
            new BuildConfig(new MainBuildConfig(null, null, null, []), null),
            null,
            SourceSpan.None
        );
    }

    private static CompilerOptions MakeOptions(
        Dictionary<string, string>? packagePaths = null,
        Dictionary<string, string>? aliases = null
    )
    {
        packagePaths ??= new Dictionary<string, string>();
        packagePaths["stdlib"] = GetStdLibPath();
        return new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            PackagePaths = packagePaths,
            ModuleAliases = aliases ?? new Dictionary<string, string>(),
        };
    }

    /// <summary>
    ///     Every source file in the package must yield exactly one compiled module. Sharper than
    ///     "it compiles": a second copy under a different name still compiles, it just duplicates
    ///     everything the file exports.
    /// </summary>
    private static void AssertOneModulePerFile(LibraryCompilationResult result, string sourceDir)
    {
        var fromPackage = result
            .Modules.Values.Where(m =>
                m.FilePath.StartsWith(sourceDir, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        var duplicated = fromPackage
            .GroupBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g =>
                $"{Path.GetFileName(g.Key)} compiled as [{string.Join(", ", g.Select(m => m.Name))}]"
            )
            .ToList();

        Assert.True(duplicated.Count == 0, string.Join("\n", duplicated));
    }

    // The spelling every real package uses today (e.g. `(import stdlib/treelist)` in stdlib).
    [Fact]
    public void PrefixedSiblingImport_IsTrackedAsADependency_AndCompiledOnce()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "helper.zs"),
                "(module helper)\n(export helper-fn)\n(define (helper-fn) : Int 7)"
            );
            File.WriteAllText(
                Path.Combine(dir, "user.zs"),
                "(module user)\n(import mypkg/helper)\n(export use-it)\n(define (use-it) : Int (helper-fn))"
            );

            var diag = new DiagnosticBag();
            var result = new LibraryCompiler(diag).Compile(
                dir,
                MakeManifest("mypkg"),
                MakeOptions()
            );

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            AssertOneModulePerFile(result, dir);
            Assert.Equal(
                ["mypkg/helper", "mypkg/user"],
                result.Modules.Keys.Where(k => k.StartsWith("mypkg")).Order()
            );
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Bare spelling of an ordinary sibling: "helper" rather than "mypkg/helper".
    [Fact]
    public void BareSiblingImport_ResolvesToThePrefixedModule_AndCompilesItOnce()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "helper.zs"),
                "(module helper)\n(export helper-fn)\n(define (helper-fn) : Int 7)"
            );
            File.WriteAllText(
                Path.Combine(dir, "user.zs"),
                "(module user)\n(import helper)\n(export use-it)\n(define (use-it) : Int (helper-fn))"
            );

            var diag = new DiagnosticBag();
            var result = new LibraryCompiler(diag).Compile(
                dir,
                MakeManifest("mypkg"),
                MakeOptions()
            );

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            AssertOneModulePerFile(result, dir);
            Assert.Contains("mypkg/helper", result.Modules.Keys);
            Assert.DoesNotContain("helper", result.Modules.Keys);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Bare spelling of the package's default module — the same import a consumer writes.
    [Fact]
    public void BareDefaultModuleImport_ResolvesToThePrefixedModule_AndCompilesItOnce()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "mypkg.zs"),
                "(module mypkg)\n(export base-fn)\n(define (base-fn) : Int 1)"
            );
            File.WriteAllText(
                Path.Combine(dir, "other.zs"),
                "(module other)\n(import mypkg)\n(export use-it)\n(define (use-it) : Int (base-fn))"
            );

            var diag = new DiagnosticBag();
            var result = new LibraryCompiler(diag).Compile(
                dir,
                MakeManifest("mypkg", "mypkg"),
                MakeOptions()
            );

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            AssertOneModulePerFile(result, dir);
            Assert.Contains("mypkg/mypkg", result.Modules.Keys);
            Assert.DoesNotContain("mypkg", result.Modules.Keys);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Nested modules are prefixed the same way ("mypkg/util/math"), so the bare spelling of a
    // nested sibling has to canonicalize too.
    [Fact]
    public void BareNestedSiblingImport_ResolvesToThePrefixedModule_AndCompilesItOnce()
    {
        var dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "util"));
            File.WriteAllText(
                Path.Combine(dir, "util", "math.zs"),
                "(module math)\n(export twice)\n(define (twice [n : Int]) : Int (* n 2))"
            );
            File.WriteAllText(
                Path.Combine(dir, "user.zs"),
                "(module user)\n(import util/math)\n(export use-it)\n(define (use-it) : Int (twice 21))"
            );

            var diag = new DiagnosticBag();
            var result = new LibraryCompiler(diag).Compile(
                dir,
                MakeManifest("mypkg"),
                MakeOptions()
            );

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            AssertOneModulePerFile(result, dir);
            Assert.Contains("mypkg/util/math", result.Modules.Keys);
            Assert.DoesNotContain("util/math", result.Modules.Keys);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A caller-supplied alias names one of this package's *dependencies*. It already beat the
    // search paths inside a sub-compilation, so it must keep beating the package's own local
    // names when a local file happens to share the alias.
    [Fact]
    public void CallerSuppliedAlias_WinsOverASameNamedLocalModule()
    {
        var dir = CreateTempDir();
        var depDir = CreateTempDir();
        try
        {
            // The dependency package "dep", whose default module is dep/dep.
            File.WriteAllText(
                Path.Combine(depDir, "dep.zs"),
                "(module dep)\n(export from-dependency)\n(define (from-dependency) : Int 99)"
            );

            // A local file that collides with the dependency's alias.
            File.WriteAllText(
                Path.Combine(dir, "dep.zs"),
                "(module dep)\n(export from-local)\n(define (from-local) : Int 1)"
            );
            File.WriteAllText(
                Path.Combine(dir, "user.zs"),
                "(module user)\n(import dep)\n(export use-it)\n(define (use-it) : Int (from-dependency))"
            );

            var diag = new DiagnosticBag();
            var result = new LibraryCompiler(diag).Compile(
                dir,
                MakeManifest("mypkg"),
                MakeOptions(
                    packagePaths: new Dictionary<string, string> { ["dep"] = depDir },
                    aliases: new Dictionary<string, string> { ["dep"] = "dep/dep" }
                )
            );

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            AssertOneModulePerFile(result, dir);

            // `(import dep)` bound the dependency, not the local mypkg/dep.
            Assert.Contains("dep/dep", result.Modules.Keys);
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(depDir, true);
        }
    }
}
