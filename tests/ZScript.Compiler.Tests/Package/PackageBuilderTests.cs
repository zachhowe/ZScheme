using Xunit;
using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Package;
using ZScript.Compiler.Pipeline;

namespace ZScript.Compiler.Tests.Package;

public class PackageBuilderTests
{
    #region Manifest Not Found

    [Fact]
    public void ManifestNotFound_ReturnsNull_AndReportsError()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.zspkg");
        var diag = new DiagnosticBag();

        var result = BuildPackage(fakePath, diag);

        Assert.Null(result);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Manifest not found"));
    }

    #endregion

    #region No Entry File

    [Fact]
    public void NoEntrySpecified_ReturnsNull_WithError()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, """
                                                  (package
                                                    (name "test-pkg")
                                                    (version "0.1.0"))
                                                  """);
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
            Assert.Contains(diag.Diagnostics, d => d.Message.Contains("No entry file specified"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Entry File Not Found

    [Fact]
    public void EntryFileNotFound_ReturnsNull_WithError()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest("missing.zs"));
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
            Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Entry file not found"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Build Config From Manifest

    [Fact]
    public void ManifestNamespace_AppearsInCSharpOutput()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest(ns: "MyApp.Gen"));
            File.WriteAllText(Path.Combine(dir, "main.zs"), MinimalZsSource);
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            var csResult = Assert.IsType<CompilationResult.CSharpOutputResult>(result);
            Assert.Contains("MyApp.Gen", csResult.CsOutput);
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
        var dir = Path.GetDirectoryName(typeof(PackageBuilderTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScript.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteManifest(string dir, string content)
    {
        var path = Path.Combine(dir, "package.zspkg");
        File.WriteAllText(path, content);
        return path;
    }

    private static CompilationResult? BuildPackage(
        string manifestPath, DiagnosticBag diag, CompilerOptions? cliOverrides = null)
    {
        var builder = new PackageBuilder(diag);
        return builder.Build(manifestPath, cliOverrides);
    }

    private static string MinimalManifest(
        string entry = "main.zs",
        string? backend = null,
        string? ns = null,
        bool includeStdlib = true)
    {
        var buildFields = "";
        if (includeStdlib)
            buildFields += $"\n    (stdlib \"{GetStdLibPath().Replace("\\", "/")}\")";
        if (backend is not null)
            buildFields += $"\n    (backend \"{backend}\")";
        if (ns is not null)
            buildFields += $"\n    (namespace \"{ns}\")";

        return $$"""
                 (package
                   (name "test-pkg")
                   (version "0.1.0")
                   (entry "{{entry}}")
                   (build{{buildFields}}))
                 """;
    }

    private const string MinimalZsSource = "(module main)\n(export entry)\n(define (entry) : Int 0)";

    #endregion

    #region Invalid Manifest

    [Fact]
    public void InvalidManifest_NotPackageForm_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, "(not-a-package)");
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void InvalidManifest_MissingName_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, """(package (version "1.0.0"))""");
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
            Assert.Contains(diag.Diagnostics, d => d.Message.Contains("name"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void InvalidManifest_SyntaxError_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, "(package");
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Successful Build

    [Fact]
    public void MinimalManifest_CSharpBackend_ReturnsOutput()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest());
            File.WriteAllText(Path.Combine(dir, "main.zs"), MinimalZsSource);
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.True(result.Success,
                string.Join("\n", result.Diagnostics.Diagnostics));
            Assert.IsType<CompilationResult.CSharpOutputResult>(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MinimalManifest_IlBackend_ReturnsOutputBytes()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest(backend: "il"));
            File.WriteAllText(Path.Combine(dir, "main.zs"), MinimalZsSource);
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.IsType<CompilationResult.IlOutputResult>(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region CLI Overrides

    [Fact]
    public void CliOverride_Namespace_SupersedesManifest()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest(ns: "ManifestNs"));
            File.WriteAllText(Path.Combine(dir, "main.zs"), MinimalZsSource);
            var diag = new DiagnosticBag();
            var overrides = new CompilerOptions { Namespace = "CliNs" };

            var result = BuildPackage(manifestPath, diag, overrides);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            var csResult = Assert.IsType<CompilationResult.CSharpOutputResult>(result);
            Assert.Contains("CliNs", csResult.CsOutput);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CliOverride_OutputMode_SupersedesManifest()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest(backend: "cs"));
            File.WriteAllText(Path.Combine(dir, "main.zs"), MinimalZsSource);
            var diag = new DiagnosticBag();
            var overrides = new CompilerOptions { OutputMode = OutputMode.Il };

            var result = BuildPackage(manifestPath, diag, overrides);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.IsType<CompilationResult.IlOutputResult>(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CliOverride_DefaultNamespace_DoesNotOverrideManifest()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest(ns: "ManifestNs"));
            File.WriteAllText(Path.Combine(dir, "main.zs"), MinimalZsSource);
            var diag = new DiagnosticBag();
            var overrides = new CompilerOptions { Namespace = "ZScriptGenerated" };

            var result = BuildPackage(manifestPath, diag, overrides);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            var csResult = Assert.IsType<CompilationResult.CSharpOutputResult>(result);
            Assert.Contains("ManifestNs", csResult.CsOutput);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CliOverride_DefaultOutputMode_DoesNotOverrideManifest()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest(backend: "il"));
            File.WriteAllText(Path.Combine(dir, "main.zs"), MinimalZsSource);
            var diag = new DiagnosticBag();
            var overrides = new CompilerOptions { OutputMode = OutputMode.CSharp };

            var result = BuildPackage(manifestPath, diag, overrides);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.IsType<CompilationResult.IlOutputResult>(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CliOverride_StdLibPath_SupersedesManifest()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest(includeStdlib: false));
            File.WriteAllText(Path.Combine(dir, "main.zs"), MinimalZsSource);
            var diag = new DiagnosticBag();
            var overrides = new CompilerOptions { StdLibPath = GetStdLibPath() };

            var result = BuildPackage(manifestPath, diag, overrides);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.True(result.Success);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region ZScript Local Dependency

    [Fact]
    public void LocalZScriptDep_ValidPath_SuccessfulBuild()
    {
        var dir = CreateTempDir();
        try
        {
            var depsDir = Path.Combine(dir, "deps");
            Directory.CreateDirectory(depsDir);
            File.WriteAllText(Path.Combine(depsDir, "helper.zs"),
                "(module helper)\n(export help-fn)\n(define (help-fn) : Int 42)");

            var manifest = $$"""
                             (package
                               (name "test-pkg")
                               (version "0.1.0")
                               (entry "main.zs")
                               (dependencies
                                 (zscript
                                   [helper :local "deps"]))
                               (build
                                 (stdlib "{{GetStdLibPath().Replace("\\", "/")}}")))
                             """;
            var manifestPath = WriteManifest(dir, manifest);
            File.WriteAllText(Path.Combine(dir, "main.zs"),
                "(module main)\n(import helper)\n(export run)\n(define (run) : Int (help-fn))");
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.False(diag.HasErrors, string.Join("\n", diag.Diagnostics));
            Assert.NotNull(result);
            Assert.True(result.Success,
                string.Join("\n", result.Diagnostics.Diagnostics));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LocalZScriptDep_NotFound_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            var manifest = $$"""
                             (package
                               (name "test-pkg")
                               (version "0.1.0")
                               (entry "main.zs")
                               (dependencies
                                 (zscript
                                   [helper :local "nonexistent-dir"]))
                               (build
                                 (stdlib "{{GetStdLibPath().Replace("\\", "/")}}")))
                             """;
            var manifestPath = WriteManifest(dir, manifest);
            File.WriteAllText(Path.Combine(dir, "main.zs"), MinimalZsSource);
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.Null(result);
            Assert.True(diag.HasErrors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion

    #region Compilation Errors

    [Fact]
    public void EntryFile_SyntaxError_ReturnsFailedResult()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest());
            File.WriteAllText(Path.Combine(dir, "main.zs"), "(define");
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.True(result is null || !result.Success);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EntryFile_TypeError_ReturnsFailedResult()
    {
        var dir = CreateTempDir();
        try
        {
            var manifestPath = WriteManifest(dir, MinimalManifest());
            File.WriteAllText(Path.Combine(dir, "main.zs"),
                "(define (main [x : Int]) : String x)");
            var diag = new DiagnosticBag();

            var result = BuildPackage(manifestPath, diag);

            Assert.True(result is null || !result.Success);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    #endregion
}
