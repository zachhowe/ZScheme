using System.Reflection;
using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Package;

public class LibraryCompilerTests
{
    #region No Source Files

    [Fact]
    public void NoZsFiles_ReturnsNull_AndReportsError()
    {
        var dir = CreateTempDir();
        try
        {
            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
            Assert.Contains(diag.Diagnostics, d => d.Message.Contains("No .zs files found"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Helpers

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(LibraryCompilerTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PackageManifest MakeManifest(
        string name = "test-pkg",
        string version = "0.1.0",
        string? ns = null,
        string? importPrefix = null,
        string? defaultModule = null,
        SourcePaths? sources = null)
    {
        return new PackageManifest(name, version, null, importPrefix, defaultModule,
            null, null, new PackageDependencies([], []), new PackageDependencies([], []),
            new BuildConfig(null, null, ns, []),
            sources,
            SourceSpan.None);
    }

    private static CompilerOptions MakeOptions()
    {
        return new CompilerOptions
        {
            OutputMode = OutputMode.Il,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        };
    }

    private static LibraryCompilationResult? CompileDir(
        string dir, DiagnosticBag diag, PackageManifest? manifest = null, CompilerOptions? options = null)
    {
        var compiler = new LibraryCompiler(diag);
        return compiler.Compile(dir, manifest ?? MakeManifest(), options ?? MakeOptions());
    }

    #endregion

    #region Single Module

    [Fact]
    public void SingleModule_ReturnsAssemblyBytesAndOneModule()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "greeter.zs"),
                "(module greeter)\n(export greet)\n(define (greet) : String \"hello\")");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.True(result.AssemblyBytes.Length > 0);
            Assert.Single(result.Modules);
            Assert.True(result.Modules.ContainsKey("greeter"));
            Assert.Contains("greet", result.Modules["greeter"].ExportedNames);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SingleModule_ProducesLoadableAssembly()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "greeter.zs"),
                "(module greeter)\n(export greet)\n(define (greet) : String \"hello\")");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.NotNull(result);
            var asm = Assembly.Load(result.AssemblyBytes);
            Assert.Contains(asm.GetTypes(), t => t.Name == "GreeterModule");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Multiple Modules

    [Fact]
    public void TwoModules_Independent_CompilesBoth()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "alpha.zs"),
                "(module alpha)\n(export a-val)\n(define (a-val) : Int 1)");
            File.WriteAllText(Path.Combine(dir, "beta.zs"),
                "(module beta)\n(export b-val)\n(define (b-val) : Int 2)");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.Equal(2, result.Modules.Count);
            Assert.True(result.Modules.ContainsKey("alpha"));
            Assert.True(result.Modules.ContainsKey("beta"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TwoModules_WithDependency_CompilesInOrder()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "base-mod.zs"),
                "(module base-mod)\n(export base-fn)\n(define (base-fn) : Int 42)");
            File.WriteAllText(Path.Combine(dir, "derived.zs"),
                "(module derived)\n(import base-mod)\n(export derived-fn)\n(define (derived-fn) : Int (base-fn))");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.Equal(2, result.Modules.Count);
            Assert.Contains("derived-fn", result.Modules["derived"].ExportedNames);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ThreeModules_DiamondDependency_CompilesAll()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "core.zs"),
                "(module core)\n(export core-fn)\n(define (core-fn) : Int 1)");
            File.WriteAllText(Path.Combine(dir, "left.zs"),
                "(module left)\n(import core)\n(export left-fn)\n(define (left-fn) : Int (core-fn))");
            File.WriteAllText(Path.Combine(dir, "right.zs"),
                "(module right)\n(import core)\n(export right-fn)\n(define (right-fn) : Int (core-fn))");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.Equal(3, result.Modules.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Namespace Augmentation

    [Fact]
    public void BuildNamespace_AddedToModuleClrNamespaces()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "mymod.zs"),
                "(module mymod)\n(export f)\n(define (f) : Int 1)");

            var diag = new DiagnosticBag();
            var manifest = MakeManifest(ns: "MyPkg.Generated");
            var result = CompileDir(dir, diag, manifest);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.Contains("MyPkg.Generated", result.Modules["mymod"].ExportedClrNamespaces);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NullBuildNamespace_NoAugmentation()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "mymod.zs"),
                "(module mymod)\n(export f)\n(define (f) : Int 1)");

            var diag = new DiagnosticBag();
            var manifest = MakeManifest(ns: null);
            var result = CompileDir(dir, diag, manifest);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            var namespaces = result.Modules["mymod"].ExportedClrNamespaces;
            Assert.DoesNotContain(namespaces, n => n is null);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BuildNamespace_AppliedToAllModules()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "mod1.zs"),
                "(module mod1)\n(export f1)\n(define (f1) : Int 1)");
            File.WriteAllText(Path.Combine(dir, "mod2.zs"),
                "(module mod2)\n(export f2)\n(define (f2) : Int 2)");

            var diag = new DiagnosticBag();
            var manifest = MakeManifest(ns: "Shared.Ns");
            var result = CompileDir(dir, diag, manifest);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            foreach (var (_, mod) in result.Modules)
                Assert.Contains("Shared.Ns", mod.ExportedClrNamespaces);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Sources.Main Subdirectory

    [Fact]
    public void SourcesMain_UsesSubdirectory()
    {
        var dir = CreateTempDir();
        try
        {
            var srcDir = Path.Combine(dir, "src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "foo.zs"),
                "(module foo)\n(export f)\n(define (f) : Int 1)");

            var diag = new DiagnosticBag();
            var manifest = MakeManifest(sources: new SourcePaths("src", null));
            var result = CompileDir(dir, diag, manifest);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.True(result.Modules.ContainsKey("foo"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SourcesMain_SubdirHasNoZsFiles_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            var srcDir = Path.Combine(dir, "src");
            Directory.CreateDirectory(srcDir);
            // Put a .zs file in the root — it should be ignored
            File.WriteAllText(Path.Combine(dir, "root.zs"),
                "(module root)\n(export f)\n(define (f) : Int 1)");

            var diag = new DiagnosticBag();
            var manifest = MakeManifest(sources: new SourcePaths("src", null));
            var result = CompileDir(dir, diag, manifest);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
            Assert.Contains(diag.Diagnostics, d => d.Message.Contains("No .zs files found"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Compilation Errors

    [Fact]
    public void SyntaxError_ReturnsNull_ErrorInDiagnostics()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.zs"),
                "(module bad)\n(define");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TypeError_ReturnsNull_ErrorInDiagnostics()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.zs"),
                "(module bad)\n(export f)\n(define (f [x : Int]) : String x)");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MissingImportedModule_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.zs"),
                "(module bad)\n(import nonexistent)\n(export f)\n(define (f) : Int 1)");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Class Name Generation

    [Fact]
    public void ModuleNameWithHyphens_ProducesCorrectClassName()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "my-utils.zs"),
                "(module my-utils)\n(export helper)\n(define (helper) : Int 1)");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.NotNull(result);
            var asm = Assembly.Load(result.AssemblyBytes);
            Assert.Contains(asm.GetTypes(), t => t.Name == "MyUtilsModule");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SimpleModuleName_ProducesCorrectClassName()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "utils.zs"),
                "(module utils)\n(export helper)\n(define (helper) : Int 1)");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.NotNull(result);
            var asm = Assembly.Load(result.AssemblyBytes);
            Assert.Contains(asm.GetTypes(), t => t.Name == "UtilsModule");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region CompileToCSharp

    [Fact]
    public void CompileToCSharp_SingleModule_ProducesCsSource()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "greeter.zs"),
                "(module greeter)\n(export greet)\n(define (greet) : String \"hello\")");

            var diag = new DiagnosticBag();
            var compiler = new LibraryCompiler(diag);
            var result = compiler.CompileToCSharp(dir, MakeManifest(ns: "Test.Greeter"), MakeOptions());

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.Contains("namespace Test.Greeter", result.CsOutput);
            Assert.Contains("GreeterModule", result.CsOutput);
            Assert.Single(result.Modules);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CompileToCSharp_ModuleWithDependency_EmitsBothClassesInSingleFile()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "base-mod.zs"),
                "(module base-mod)\n(export base-fn)\n(define (base-fn) : Int 42)");
            File.WriteAllText(Path.Combine(dir, "derived.zs"),
                "(module derived)\n(import base-mod)\n(export derived-fn)\n(define (derived-fn) : Int (base-fn))");

            var diag = new DiagnosticBag();
            var compiler = new LibraryCompiler(diag);
            var result = compiler.CompileToCSharp(dir, MakeManifest(), MakeOptions());

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.Contains("BaseModModule", result.CsOutput);
            Assert.Contains("DerivedModule", result.CsOutput);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CompileToCSharp_NoSourceFiles_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            var diag = new DiagnosticBag();
            var compiler = new LibraryCompiler(diag);
            var result = compiler.CompileToCSharp(dir, MakeManifest(), MakeOptions());

            Assert.Null(result);
            Assert.True(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EmptyModuleBody_CompilesSuccessfully()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "empty.zs"), "(module empty)");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.True(result.Modules.ContainsKey("empty"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ModuleDeclarationMissing_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "nomod.zs"),
                "(define (f) : Int 42)");

            var diag = new DiagnosticBag();
            var result = CompileDir(dir, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion
}
