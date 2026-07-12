using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Package;

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
              (description "A sample application")
              (license "MIT")
              (dependencies
                (nuget
                  [Newtonsoft.Json "13.0.3"]
                  [Serilog "4.0.0"])
                (zscheme
                  [utils :git "https://github.com/user/utils" "1.2.0"]
                  [my-lib :local "../my-lib"]))
              (build
                (main
                  (output "bin/my-app")
                  (backend "cs")
                  (namespace "MyApp")
                  (ref "../deps/bin"))))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Equal("my-app", manifest!.Name);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("main.zs", manifest.Entry);
        Assert.Equal("A sample application", manifest.Description);
        Assert.Equal("MIT", manifest.License);

        Assert.Equal(2, manifest.Dependencies.NuGet.Count);
        Assert.Equal("Newtonsoft.Json", manifest.Dependencies.NuGet[0].PackageId);
        Assert.Equal("13.0.3", manifest.Dependencies.NuGet[0].Version);
        Assert.Equal("Serilog", manifest.Dependencies.NuGet[1].PackageId);
        Assert.Equal("4.0.0", manifest.Dependencies.NuGet[1].Version);

        Assert.Equal(2, manifest.Dependencies.ZScheme.Count);
        Assert.Equal("utils", manifest.Dependencies.ZScheme[0].Name);
        var gitSource = Assert.IsType<ZSchemeDependencySource.Git>(
            manifest.Dependencies.ZScheme[0].Source
        );
        Assert.Equal("https://github.com/user/utils", gitSource.Url);
        Assert.Equal("1.2.0", gitSource.VersionOrRef);

        Assert.Equal("my-lib", manifest.Dependencies.ZScheme[1].Name);
        var localSource = Assert.IsType<ZSchemeDependencySource.Local>(
            manifest.Dependencies.ZScheme[1].Source
        );
        Assert.Equal("../my-lib", localSource.Path);

        Assert.NotNull(manifest.Build.Main);
        Assert.Equal("bin/my-app", manifest.Build.Main!.OutputPath);
        Assert.Equal(OutputMode.CSharp, manifest.Build.Main.Backend);
        Assert.Equal("MyApp", manifest.Build.Main.Namespace);
        Assert.Single(manifest.Build.Main.RefPaths);
        Assert.Equal("../deps/bin", manifest.Build.Main.RefPaths[0]);
        Assert.Null(manifest.Build.Test);
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
        Assert.Null(manifest.Description);
        Assert.Null(manifest.License);
        Assert.Empty(manifest.Dependencies.NuGet);
        Assert.Empty(manifest.Dependencies.ZScheme);
        Assert.Null(manifest.Build.Main);
        Assert.Null(manifest.Build.Test);
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
    public void ParsesManifestWithDescription()
    {
        var source = """
            (package
              (name "hello")
              (version "0.1.0")
              (description "A greeting application"))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Equal("A greeting application", manifest!.Description);
        Assert.Null(manifest.License);
    }

    [Fact]
    public void ParsesManifestWithLicense()
    {
        var source = """
            (package
              (name "hello")
              (version "0.1.0")
              (license "Apache-2.0"))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.Description);
        Assert.Equal("Apache-2.0", manifest.License);
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
    public void MalformedZSchemeDep_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (dependencies
                (zscheme
                  [utils])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Empty(manifest!.Dependencies.ZScheme);
    }

    [Fact]
    public void ParsesFrameworkDependencies()
    {
        var source = """
            (package
              (name "web-app")
              (version "0.1.0")
              (dependencies
                (framework Microsoft.AspNetCore.App)))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Single(manifest!.Dependencies.Frameworks);
        Assert.Equal("Microsoft.AspNetCore.App", manifest.Dependencies.Frameworks[0].Id);
    }

    [Fact]
    public void ParsesMultipleFrameworkDependencies()
    {
        var source = """
            (package
              (name "web-app")
              (version "0.1.0")
              (dependencies
                (framework Microsoft.AspNetCore.App Microsoft.WindowsDesktop.App)))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.Dependencies.Frameworks.Count);
        Assert.Equal("Microsoft.AspNetCore.App", manifest.Dependencies.Frameworks[0].Id);
        Assert.Equal("Microsoft.WindowsDesktop.App", manifest.Dependencies.Frameworks[1].Id);
    }

    [Fact]
    public void ParsesMainBuildSdkOverride()
    {
        var source = """
            (package
              (name "web-app")
              (version "0.1.0")
              (build
                (main
                  (sdk "Microsoft.NET.Sdk.Web"))))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Equal("Microsoft.NET.Sdk.Web", manifest!.Build.Main!.Sdk);
    }

    [Fact]
    public void UnknownField_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (author "someone"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("author")
        );
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
        Assert.Empty(manifest.Dependencies.ZScheme);
    }

    [Fact]
    public void ZSchemeOnlyDependencies()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (dependencies
                (zscheme
                  [utils :local "../utils"])))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Empty(manifest!.Dependencies.NuGet);
        Assert.Single(manifest.Dependencies.ZScheme);
    }

    [Fact]
    public void BuildConfig_MainAllFields()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (build
                (main
                  (output "out/app")
                  (backend "il")
                  (namespace "MyNs")
                  (ref "./libs"))))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Build.Main);
        Assert.Equal("out/app", manifest.Build.Main!.OutputPath);
        Assert.Equal(OutputMode.Il, manifest.Build.Main.Backend);
        Assert.Equal("MyNs", manifest.Build.Main.Namespace);
        Assert.Single(manifest.Build.Main.RefPaths);
        Assert.Equal("./libs", manifest.Build.Main.RefPaths[0]);
        Assert.Null(manifest.Build.Test);
    }

    [Fact]
    public void BuildConfig_MainPartialFields()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (build
                (main
                  (output "out/app"))))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Build.Main);
        Assert.Equal("out/app", manifest.Build.Main!.OutputPath);
        Assert.Null(manifest.Build.Main.Backend);
        Assert.Null(manifest.Build.Main.Namespace);
        Assert.Empty(manifest.Build.Main.RefPaths);
        Assert.Null(manifest.Build.Test);
    }

    [Fact]
    public void BuildConfig_TestAllFields()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (build
                (test
                  (output "out/test")
                  (namespace "MyNs.Tests")
                  (ref "./mocks")
                  (ref "./fixtures"))))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.Build.Main);
        Assert.NotNull(manifest.Build.Test);
        Assert.Equal("out/test", manifest.Build.Test!.OutputPath);
        Assert.Equal("MyNs.Tests", manifest.Build.Test.Namespace);
        Assert.Equal(2, manifest.Build.Test.RefPaths.Count);
        Assert.Equal("./mocks", manifest.Build.Test.RefPaths[0]);
        Assert.Equal("./fixtures", manifest.Build.Test.RefPaths[1]);
    }

    [Fact]
    public void BuildConfig_MainAndTest()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (entry "main.zs")
              (build
                (main
                  (namespace "MyNs"))
                (test
                  (namespace "MyNs.Tests"))))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Build.Main);
        Assert.NotNull(manifest.Build.Test);
        Assert.Equal("MyNs", manifest.Build.Main!.Namespace);
        Assert.Equal("MyNs.Tests", manifest.Build.Test!.Namespace);
    }

    [Fact]
    public void BuildConfig_TestRejectsBackend()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (build
                (test
                  (backend "csharp"))))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Message.Contains("'backend' is not supported in test build config")
        );
    }

    [Fact]
    public void BuildConfig_FlatFormIsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (build
                (namespace "MyNs")))
            """;

        var diag = new DiagnosticBag();
        Parse(source, diag);

        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Message.Contains("must be nested under (main ...) or (test ...)")
        );
    }

    [Fact]
    public void BuildConfig_MissingBothIsNull()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0"))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.Build.Main);
        Assert.Null(manifest.Build.Test);
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
                (zscheme
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
        Assert.Contains(
            diag.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("optimize")
        );
    }

    // --- Warning tests ---

    [Fact]
    public void ExtraTopLevelForms_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0"))
            (package
              (name "other")
              (version "2.0.0"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("Extra top-level forms")
        );
    }

    [Fact]
    public void NonSectionItem_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              "stray-string")
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("Expected a section form")
        );
    }

    [Fact]
    public void NonKeywordSection_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              ("not-a-keyword" "value"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("Expected a keyword")
        );
    }

    [Fact]
    public void InvalidDependencyItem_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                "not-a-list"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains(
                    "Expected (nuget ...), (zscheme ...), or (framework ...) section"
                )
        );
    }

    [Fact]
    public void NonSymbolDependencyKeyword_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                ("not-symbol" [Pkg "1.0"])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("Expected 'nuget', 'zscheme', or 'framework'")
        );
    }

    [Fact]
    public void UnknownDependencySection_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (maven [Pkg "1.0"])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("maven")
        );
    }

    [Fact]
    public void InvalidSourcesItem_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (sources
                "not-a-list"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("Expected (key \"value\") in sources section")
        );
    }

    [Fact]
    public void NonKeywordSourcesField_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (sources
                ("not-keyword" "src")))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("Expected sources field keyword")
        );
    }

    [Fact]
    public void UnknownSourcesField_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (sources
                (docs "docs")))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("docs")
        );
    }

    [Fact]
    public void InvalidBuildItem_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (build
                (main
                  "not-a-list")))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("Expected (key \"value\") in main build section")
        );
    }

    [Fact]
    public void NonKeywordBuildField_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (build
                (main
                  ("not-keyword" "value"))))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d =>
                d.Severity == DiagnosticSeverity.Warning
                && d.Message.Contains("Expected main build field keyword")
        );
    }

    [Fact]
    public void DeprecatedStdlibField_ProducesWarning()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (build
                (main
                  (stdlib "../stdlib"))))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.False(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("deprecated")
        );
    }

    // --- NuGet dependency error tests ---

    [Fact]
    public void NuGetDep_WrongItemCount_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (nuget
                  [Pkg "1.0" "extra"])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("NuGet dependency must be"));
    }

    [Fact]
    public void NuGetDep_NonSymbolPackageId_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (nuget
                  ["Pkg" "1.0"])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Expected package ID symbol"));
    }

    [Fact]
    public void NuGetDep_NonStringVersion_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (nuget
                  [Pkg 123])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Expected version string"));
    }

    // --- ZScheme dependency error tests ---

    [Fact]
    public void ZSchemeDep_NonSymbolName_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (zscheme
                  ["utils" :git "url" "1.0"])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Message.Contains("Expected dependency name symbol")
        );
    }

    [Fact]
    public void ZSchemeDep_MissingColonToken_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (zscheme
                  [utils git "url" "1.0"])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Expected ':'"));
    }

    [Fact]
    public void ZSchemeDep_MissingSourceType_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (zscheme
                  [utils : "url"])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Expected source type"));
    }

    // --- Git dependency error tests ---

    [Fact]
    public void GitDep_TooFewItems_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (zscheme
                  [utils :git "url"])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Git dependency must be"));
    }

    [Fact]
    public void GitDep_NonStringUrl_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (zscheme
                  [utils :git url "1.0"])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Expected URL string"));
    }

    [Fact]
    public void GitDep_NonStringVersion_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (zscheme
                  [utils :git "url" version])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Expected version string"));
    }

    // --- Local dependency error tests ---

    [Fact]
    public void LocalDep_TooFewItems_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (zscheme
                  [utils :local])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Expected source type"));
    }

    [Fact]
    public void LocalDep_NonStringPath_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (zscheme
                  [utils :local path])))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Expected path string"));
    }

    // --- Package field error tests ---

    [Fact]
    public void PackageField_MissingValue_ReportsError()
    {
        var source = """
            (package
              (name)
              (version "1.0.0"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.Null(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Expected a value for 'name'"));
    }

    [Fact]
    public void PackageField_NonStringValue_ReportsError()
    {
        var source = """
            (package
              (name app)
              (version "1.0.0"))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.Null(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Message.Contains("Expected a string value for 'name'")
        );
    }

    // --- Build field error tests ---

    [Fact]
    public void ParsesTestDependencies()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (test-dependencies
                (nuget
                  [xunit "2.9.3"])
                (zscheme
                  [zunit :local "../zunit"])))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Single(manifest!.TestDependencies.NuGet);
        Assert.Equal("xunit", manifest.TestDependencies.NuGet[0].PackageId);
        Assert.Equal("2.9.3", manifest.TestDependencies.NuGet[0].Version);
        Assert.Single(manifest.TestDependencies.ZScheme);
        Assert.Equal("zunit", manifest.TestDependencies.ZScheme[0].Name);
        var localSource = Assert.IsType<ZSchemeDependencySource.Local>(
            manifest.TestDependencies.ZScheme[0].Source
        );
        Assert.Equal("../zunit", localSource.Path);
    }

    [Fact]
    public void TestDependencies_DefaultsToEmpty()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0"))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Empty(manifest!.TestDependencies.NuGet);
        Assert.Empty(manifest.TestDependencies.ZScheme);
    }

    [Fact]
    public void TestDependencies_IndependentOfDependencies()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (dependencies
                (nuget
                  [Newtonsoft.Json "13.0.3"]))
              (test-dependencies
                (zscheme
                  [zunit :local "../zunit"])))
            """;

        var manifest = Parse(source);

        Assert.NotNull(manifest);
        Assert.Single(manifest!.Dependencies.NuGet);
        Assert.Empty(manifest.Dependencies.ZScheme);
        Assert.Empty(manifest.TestDependencies.NuGet);
        Assert.Single(manifest.TestDependencies.ZScheme);
    }

    [Fact]
    public void BuildField_NonStringValue_ReportsError()
    {
        var source = """
            (package
              (name "app")
              (version "1.0.0")
              (build
                (main
                  (output 123))))
            """;

        var diag = new DiagnosticBag();
        var manifest = Parse(source, diag);

        Assert.NotNull(manifest);
        Assert.True(diag.HasErrors);
        Assert.Contains(
            diag.Diagnostics,
            d => d.Message.Contains("Expected a string value for build field 'output'")
        );
    }

    [Fact]
    public void ParsesWarnUnusedParamsField()
    {
        var manifest = Parse(
            """
            (package
              (name "my-app")
              (version "1.0.0")
              (build
                (main
                  (warn-unused-params "false"))))
            """
        );

        Assert.NotNull(manifest);
        Assert.False(manifest!.Build.Main!.WarnUnusedParameters);
    }

    [Fact]
    public void WarnUnusedParams_InvalidValue_WarnsAndLeavesUnset()
    {
        var diag = new DiagnosticBag();
        var manifest = Parse(
            """
            (package
              (name "my-app")
              (version "1.0.0")
              (build
                (main
                  (warn-unused-params "maybe"))))
            """,
            diag
        );

        Assert.NotNull(manifest);
        Assert.Null(manifest!.Build.Main!.WarnUnusedParameters);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("warn-unused-params"));
    }
}
