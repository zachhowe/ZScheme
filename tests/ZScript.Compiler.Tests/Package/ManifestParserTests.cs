namespace ZScript.Compiler.Tests.Package;

using ZScript.Compiler.Diagnostics;
using ZScript.Compiler.Package;
using ZScript.Compiler.Pipeline;
using Xunit;

public class ManifestParserTests
{
    private static PackageManifest? Parse(string source, DiagnosticBag? diag = null)
    {
        diag ??= new DiagnosticBag();
        var parser = new ManifestParser(diag);
        return parser.Parse(source);
    }

    [Fact]
    public void ParsesFullManifest()
    {
        var source = """
            (package
              (name "my-app")
              (version "1.0.0")
              (entry "main.zs")
              (dependencies
                (nuget
                  [Newtonsoft.Json "13.0.3"]
                  [Serilog "4.0.0"])
                (zscript
                  [utils :git "https://github.com/user/utils" "1.2.0"]
                  [my-lib :local "../my-lib"]))
              (build
                (output "bin/my-app")
                (backend "cs")
                (namespace "MyApp")
                (stdlib "../stdlib")
                (ref "../deps/bin")))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Equal("my-app", manifest!.Name);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("main.zs", manifest.Entry);

        Assert.Equal(2, manifest.Dependencies.NuGet.Count);
        Assert.Equal("Newtonsoft.Json", manifest.Dependencies.NuGet[0].PackageId);
        Assert.Equal("13.0.3", manifest.Dependencies.NuGet[0].Version);
        Assert.Equal("Serilog", manifest.Dependencies.NuGet[1].PackageId);
        Assert.Equal("4.0.0", manifest.Dependencies.NuGet[1].Version);

        Assert.Equal(2, manifest.Dependencies.ZScript.Count);
        Assert.Equal("utils", manifest.Dependencies.ZScript[0].Name);
        var gitSource = Assert.IsType<ZScriptDependencySource.Git>(manifest.Dependencies.ZScript[0].Source);
        Assert.Equal("https://github.com/user/utils", gitSource.Url);
        Assert.Equal("1.2.0", gitSource.VersionOrRef);

        Assert.Equal("my-lib", manifest.Dependencies.ZScript[1].Name);
        var localSource = Assert.IsType<ZScriptDependencySource.Local>(manifest.Dependencies.ZScript[1].Source);
        Assert.Equal("../my-lib", localSource.Path);

        Assert.Equal("bin/my-app", manifest.Build.OutputPath);
        Assert.Equal(OutputMode.CSharp, manifest.Build.Backend);
        Assert.Equal("MyApp", manifest.Build.Namespace);
        Assert.Equal("../stdlib", manifest.Build.StdLibPath);
        Assert.Single(manifest.Build.RefPaths);
        Assert.Equal("../deps/bin", manifest.Build.RefPaths[0]);
    }

    [Fact]
    public void ParsesMinimalManifest()
    {
        var source = """
            (package
              (name "hello")
              (version "0.1.0"))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Equal("hello", manifest!.Name);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Null(manifest.Entry);
        Assert.Empty(manifest.Dependencies.NuGet);
        Assert.Empty(manifest.Dependencies.ZScript);
        Assert.Null(manifest.Build.OutputPath);
        Assert.Null(manifest.Build.Backend);
        Assert.Null(manifest.Build.Namespace);
        Assert.Null(manifest.Build.StdLibPath);
        Assert.Empty(manifest.Build.RefPaths);
        Assert.Null(manifest.Sources);
    }

    [Fact]
    public void ParsesSourcePaths()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (sources
                (main "src")
                (test "test")))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Sources);
        Assert.Equal("src", manifest.Sources!.Main);
        Assert.Equal("test", manifest.Sources.Test);
    }

    [Fact]
    public void ParsesSourcePaths_MainOnly()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (sources
                (main "src")))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Sources);
        Assert.Equal("src", manifest.Sources!.Main);
        Assert.Null(manifest.Sources.Test);
    }

    [Fact]
    public void ParsesManifestWithEntry()
    {
        var source = """
            (package
              (name "hello")
              (version "0.1.0")
              (entry "hello.zs"))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Equal("hello", manifest!.Name);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal("hello.zs", manifest.Entry);
    }

    [Fact]
    public void MissingName_ReportsError()
    {
        var source = """
            (package
              (version "1.0.0")
              (entry "main.zs"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.Null(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("name"));
    }

    [Fact]
    public void MissingVersion_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (entry "main.zs"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.Null(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("version"));
    }

    [Fact]
    public void MissingEntry_Succeeds()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Null(manifest!.Entry);
    }

    [Fact]
    public void MalformedNuGetDep_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (dependencies
                (nuget
                  [Newtonsoft.Json])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Empty(manifest!.Dependencies.NuGet);
    }

    [Fact]
    public void MalformedZScriptDep_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (dependencies
                (zscript
                  [utils])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Empty(manifest!.Dependencies.ZScript);
    }

    [Fact]
    public void UnknownField_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (description "A test app"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("description"));
    }

    [Fact]
    public void NuGetOnlyDependencies()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (dependencies
                (nuget
                  [Newtonsoft.Json "13.0.3"])))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Single(manifest!.Dependencies.NuGet);
        Assert.Empty(manifest.Dependencies.ZScript);
    }

    [Fact]
    public void ZScriptOnlyDependencies()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (dependencies
                (zscript
                  [utils :local "../utils"])))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Empty(manifest!.Dependencies.NuGet);
        Assert.Single(manifest.Dependencies.ZScript);
    }

    [Fact]
    public void BuildConfig_AllFields()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (build
                (output "out/app")
                (backend "il")
                (namespace "MyNs")
                (stdlib "./std")
                (ref "./libs")))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Equal("out/app", manifest!.Build.OutputPath);
        Assert.Equal(OutputMode.IL, manifest.Build.Backend);
        Assert.Equal("MyNs", manifest.Build.Namespace);
        Assert.Equal("./std", manifest.Build.StdLibPath);
        Assert.Single(manifest.Build.RefPaths);
        Assert.Equal("./libs", manifest.Build.RefPaths[0]);
    }

    [Fact]
    public void BuildConfig_PartialFields()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (build
                (output "out/app")))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Equal("out/app", manifest!.Build.OutputPath);
        Assert.Null(manifest.Build.Backend);
        Assert.Null(manifest.Build.Namespace);
        Assert.Null(manifest.Build.StdLibPath);
        Assert.Empty(manifest.Build.RefPaths);
    }

    [Fact]
    public void EmptyInput_ReportsError()
    {
        var diag = new DiagnosticBag();
        var manifest = Parse("", diag);

        Assert.Null(manifest);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void NotPackageForm_ReportsError()
    {
        var source = "(define x 1)";
        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.Null(manifest);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public void UnknownDependencySource_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (dependencies
                (zscript
                  [utils :npm "some-pkg" "1.0.0"])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("npm"));
    }

    [Fact]
    public void UnknownBuildField_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (build
                (optimize "true")))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("optimize"));
    }
}
