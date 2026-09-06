using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Package;

public class ManifestSerializerTests
{
    private static PackageManifest? RoundTrip(PackageManifest manifest)
    {
        var serialized = ManifestSerializer.Serialize(manifest);
        var diag = new DiagnosticBag();
        var parser = new ManifestParser(diag);
        var result = parser.Parse(serialized);
        Assert.False(
            diag.HasErrors,
            $"Parse errors after round-trip:\n{string.Join("\n", diag.Diagnostics)}"
        );
        return result;
    }

    private static PackageManifest MakeManifest(
        string name = "my-pkg",
        string version = "0.1.0",
        string? entry = null,
        string? importPrefix = null,
        string? defaultModule = null,
        string? description = null,
        string? license = null,
        PackageDependencies? deps = null,
        PackageDependencies? testDeps = null,
        BuildConfig? build = null,
        SourcePaths? sources = null
    )
    {
        return new PackageManifest(
            name,
            version,
            entry,
            importPrefix,
            defaultModule,
            description,
            license,
            deps ?? new PackageDependencies([], []),
            testDeps ?? new PackageDependencies([], []),
            build ?? new BuildConfig(null, null),
            sources,
            SourceSpan.None
        );
    }

    [Fact]
    public void SerializesMinimalManifest()
    {
        var manifest = MakeManifest();
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Equal(
            """
            (package
              (name "my-pkg")
              (version "0.1.0"))

            """.ReplaceLineEndings(),
            output
        );
    }

    [Fact]
    public void SerializesMinimalManifest_RoundTrips()
    {
        var manifest = MakeManifest();
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Equal("my-pkg", parsed!.Name);
        Assert.Equal("0.1.0", parsed.Version);
    }

    [Fact]
    public void SerializesAllStringFields()
    {
        var manifest = MakeManifest(
            "full-pkg",
            "1.2.3",
            "src/main.zs",
            "full",
            "main",
            "A full package",
            "MIT"
        );
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains("""(name "full-pkg")""", output);
        Assert.Contains("""(version "1.2.3")""", output);
        Assert.Contains("""(entry "src/main.zs")""", output);
        Assert.Contains("""(import-prefix "full")""", output);
        Assert.Contains("""(default-module "main")""", output);
        Assert.Contains("""(description "A full package")""", output);
        Assert.Contains("""(license "MIT")""", output);
    }

    [Fact]
    public void SerializesAllStringFields_RoundTrips()
    {
        var manifest = MakeManifest(
            "full-pkg",
            "1.2.3",
            "src/main.zs",
            "full",
            "main",
            "A full package",
            "MIT"
        );
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Equal("full-pkg", parsed!.Name);
        Assert.Equal("1.2.3", parsed.Version);
        Assert.Equal("src/main.zs", parsed.Entry);
        Assert.Equal("full", parsed.ImportPrefix);
        Assert.Equal("main", parsed.DefaultModule);
        Assert.Equal("A full package", parsed.Description);
        Assert.Equal("MIT", parsed.License);
    }

    [Fact]
    public void SerializesSourcePaths()
    {
        var manifest = MakeManifest(sources: new SourcePaths("src", "test"));
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains(
            """
              (sources
                (main "src")
                (test "test"))
            """.ReplaceLineEndings(),
            output
        );
    }

    [Fact]
    public void SerializesSourcePaths_RoundTrips()
    {
        var manifest = MakeManifest(sources: new SourcePaths("src", "test"));
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Sources);
        Assert.Equal("src", parsed.Sources!.Main);
        Assert.Equal("test", parsed.Sources.Test);
    }

    [Fact]
    public void SerializesSourcePaths_MainOnly()
    {
        var manifest = MakeManifest(sources: new SourcePaths("lib", null));
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains("""(main "lib")""", output);
        Assert.DoesNotContain("test", output);
    }

    [Fact]
    public void SerializesNuGetDependencies()
    {
        var deps = new PackageDependencies(
            [],
            [
                new NuGetDependency("System.Collections.Immutable", "9.0.0", SourceSpan.None),
                new NuGetDependency("xunit", "2.9.3", SourceSpan.None),
            ]
        );
        var manifest = MakeManifest(deps: deps);
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains("""[System.Collections.Immutable "9.0.0"]""", output);
        Assert.Contains("""[xunit "2.9.3"]""", output);
    }

    [Fact]
    public void SerializesNuGetDependencies_RoundTrips()
    {
        var deps = new PackageDependencies(
            [],
            [new NuGetDependency("System.Collections.Immutable", "9.0.0", SourceSpan.None)]
        );
        var manifest = MakeManifest(deps: deps);
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Dependencies.NuGet);
        Assert.Equal("System.Collections.Immutable", parsed.Dependencies.NuGet[0].PackageId);
        Assert.Equal("9.0.0", parsed.Dependencies.NuGet[0].Version);
    }

    [Fact]
    public void SerializesLocalZSchemeDependency()
    {
        var deps = new PackageDependencies(
            [
                new ZSchemeDependency(
                    "stdlib",
                    new ZSchemeDependencySource.Local("../stdlib"),
                    SourceSpan.None
                ),
            ],
            []
        );
        var manifest = MakeManifest(deps: deps);
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains("""[stdlib :local "../stdlib"]""", output);
    }

    [Fact]
    public void SerializesLocalZSchemeDependency_RoundTrips()
    {
        var deps = new PackageDependencies(
            [
                new ZSchemeDependency(
                    "stdlib",
                    new ZSchemeDependencySource.Local("../stdlib"),
                    SourceSpan.None
                ),
            ],
            []
        );
        var manifest = MakeManifest(deps: deps);
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Dependencies.ZScheme);
        Assert.Equal("stdlib", parsed.Dependencies.ZScheme[0].Name);
        var local = Assert.IsType<ZSchemeDependencySource.Local>(
            parsed.Dependencies.ZScheme[0].Source
        );
        Assert.Equal("../stdlib", local.Path);
    }

    [Fact]
    public void SerializesGitZSchemeDependency()
    {
        var deps = new PackageDependencies(
            [
                new ZSchemeDependency(
                    "utils",
                    new ZSchemeDependencySource.Git("https://github.com/user/utils", "v1.0.0"),
                    SourceSpan.None
                ),
            ],
            []
        );
        var manifest = MakeManifest(deps: deps);
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains("""[utils :git "https://github.com/user/utils" "v1.0.0"]""", output);
    }

    [Fact]
    public void SerializesGitZSchemeDependency_RoundTrips()
    {
        var deps = new PackageDependencies(
            [
                new ZSchemeDependency(
                    "utils",
                    new ZSchemeDependencySource.Git("https://github.com/user/utils", "v1.0.0"),
                    SourceSpan.None
                ),
            ],
            []
        );
        var manifest = MakeManifest(deps: deps);
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Dependencies.ZScheme);
        var git = Assert.IsType<ZSchemeDependencySource.Git>(parsed.Dependencies.ZScheme[0].Source);
        Assert.Equal("https://github.com/user/utils", git.Url);
        Assert.Equal("v1.0.0", git.VersionOrRef);
    }

    [Fact]
    public void SerializesTestDependencies()
    {
        var testDeps = new PackageDependencies(
            [
                new ZSchemeDependency(
                    "zunit",
                    new ZSchemeDependencySource.Local("../zunit"),
                    SourceSpan.None
                ),
            ],
            []
        );
        var manifest = MakeManifest(testDeps: testDeps);
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains("(test-dependencies", output);
        Assert.Contains("""[zunit :local "../zunit"]""", output);
    }

    [Fact]
    public void SerializesTestDependencies_RoundTrips()
    {
        var testDeps = new PackageDependencies(
            [
                new ZSchemeDependency(
                    "zunit",
                    new ZSchemeDependencySource.Local("../zunit"),
                    SourceSpan.None
                ),
            ],
            []
        );
        var manifest = MakeManifest(testDeps: testDeps);
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Single(parsed!.TestDependencies.ZScheme);
        Assert.Equal("zunit", parsed.TestDependencies.ZScheme[0].Name);
    }

    /// <summary>
    ///     The warn opt-outs are the only tri-state fields in the main block: null means "not
    ///     specified", so a manifest that sets one has to survive a round trip as `false`
    ///     rather than collapsing back to the compiler default.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializesWarnDeprecatedKeyword_RoundTrips(bool warn)
    {
        var build = new BuildConfig(
            new MainBuildConfig(null, null, null, [], WarnDeprecatedKeyword: warn),
            null
        );
        var manifest = MakeManifest(build: build);

        var output = ManifestSerializer.Serialize(manifest);
        Assert.Contains($"(warn-deprecated-keyword \"{(warn ? "true" : "false")}\")", output);

        var parsed = RoundTrip(manifest);
        Assert.Equal(warn, parsed!.Build!.Main!.WarnDeprecatedKeyword);
    }

    /// <summary>Left unset, it stays unset — the main block must not gain a stray field.</summary>
    [Fact]
    public void OmitsWarnDeprecatedKeyword_WhenUnset()
    {
        var manifest = MakeManifest(
            build: new BuildConfig(new MainBuildConfig(null, null, "MyApp", []), null)
        );

        var output = ManifestSerializer.Serialize(manifest);
        Assert.DoesNotContain("warn-deprecated-keyword", output);
        Assert.Null(RoundTrip(manifest)!.Build!.Main!.WarnDeprecatedKeyword);
    }

    [Fact]
    public void SerializesMainBuildConfig()
    {
        var build = new BuildConfig(
            new MainBuildConfig("output.cs", OutputMode.CSharp, "MyApp.Generated", ["lib/ref.dll"]),
            null
        );
        var manifest = MakeManifest(build: build);
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains("(main", output);
        Assert.Contains("""(namespace "MyApp.Generated")""", output);
        Assert.Contains("""(output "output.cs")""", output);
        Assert.Contains("""(backend "csharp")""", output);
        Assert.Contains("""(ref "lib/ref.dll")""", output);
    }

    [Fact]
    public void SerializesMainBuildConfig_RoundTrips()
    {
        var build = new BuildConfig(
            new MainBuildConfig("output.cs", OutputMode.CSharp, "MyApp.Generated", ["lib/ref.dll"]),
            null
        );
        var manifest = MakeManifest(build: build);
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Build.Main);
        Assert.Equal("MyApp.Generated", parsed.Build.Main!.Namespace);
        Assert.Equal("output.cs", parsed.Build.Main.OutputPath);
        Assert.Equal(OutputMode.CSharp, parsed.Build.Main.Backend);
        Assert.Single(parsed.Build.Main.RefPaths);
        Assert.Equal("lib/ref.dll", parsed.Build.Main.RefPaths[0]);
        Assert.Null(parsed.Build.Test);
    }

    [Fact]
    public void SerializesMainBuildConfig_IlBackend()
    {
        var build = new BuildConfig(new MainBuildConfig(null, OutputMode.Il, null, []), null);
        var manifest = MakeManifest(build: build);
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains("""(backend "il")""", output);
    }

    [Fact]
    public void SerializesTestBuildConfig_RoundTrips()
    {
        var build = new BuildConfig(
            null,
            new TestBuildConfig("out/test", "MyApp.Tests", ["mocks/Foo.dll"])
        );
        var manifest = MakeManifest(build: build);
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Null(parsed!.Build.Main);
        Assert.NotNull(parsed.Build.Test);
        Assert.Equal("MyApp.Tests", parsed.Build.Test!.Namespace);
        Assert.Equal("out/test", parsed.Build.Test.OutputPath);
        Assert.Single(parsed.Build.Test.RefPaths);
        Assert.Equal("mocks/Foo.dll", parsed.Build.Test.RefPaths[0]);
    }

    [Fact]
    public void SerializesBothSubsections_MainBeforeTest()
    {
        var build = new BuildConfig(
            new MainBuildConfig(null, null, "MyApp", []),
            new TestBuildConfig(null, "MyApp.Tests", [])
        );
        var manifest = MakeManifest(build: build);
        var output = ManifestSerializer.Serialize(manifest);

        var mainIdx = output.IndexOf("(main", StringComparison.Ordinal);
        var testIdx = output.IndexOf("(test", StringComparison.Ordinal);
        Assert.True(mainIdx >= 0);
        Assert.True(testIdx > mainIdx);
    }

    [Fact]
    public void SerializesBothSubsections_RoundTrips()
    {
        var build = new BuildConfig(
            new MainBuildConfig(null, OutputMode.Il, "MyApp", ["main.dll"]),
            new TestBuildConfig(null, "MyApp.Tests", ["test.dll"])
        );
        var manifest = MakeManifest(build: build);
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Equal("MyApp", parsed!.Build.Main!.Namespace);
        Assert.Equal("MyApp.Tests", parsed.Build.Test!.Namespace);
        Assert.Equal(OutputMode.Il, parsed.Build.Main.Backend);
        Assert.Single(parsed.Build.Main.RefPaths);
        Assert.Single(parsed.Build.Test.RefPaths);
    }

    [Fact]
    public void SerializesFrameworkDependencies()
    {
        var deps = new PackageDependencies(
            [],
            [],
            [new FrameworkDependency("Microsoft.AspNetCore.App", SourceSpan.None)]
        );
        var manifest = MakeManifest(deps: deps);
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains("(framework Microsoft.AspNetCore.App)", output);
    }

    [Fact]
    public void SerializesFrameworkDependencies_RoundTrips()
    {
        var deps = new PackageDependencies(
            [],
            [],
            [new FrameworkDependency("Microsoft.AspNetCore.App", SourceSpan.None)]
        );
        var manifest = MakeManifest(deps: deps);
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Dependencies.Frameworks);
        Assert.Equal("Microsoft.AspNetCore.App", parsed.Dependencies.Frameworks[0].Id);
    }

    [Fact]
    public void SerializesMainBuildSdk_RoundTrips()
    {
        var build = new BuildConfig(
            new MainBuildConfig(null, null, "MyApp", [], "Microsoft.NET.Sdk.Web"),
            null
        );
        var manifest = MakeManifest(build: build);
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Equal("Microsoft.NET.Sdk.Web", parsed!.Build.Main!.Sdk);
    }

    [Fact]
    public void OmitsEmptyDependencies()
    {
        var manifest = MakeManifest();
        var output = ManifestSerializer.Serialize(manifest);

        Assert.DoesNotContain("dependencies", output);
    }

    [Fact]
    public void OmitsEmptyBuildConfig()
    {
        var manifest = MakeManifest();
        var output = ManifestSerializer.Serialize(manifest);

        Assert.DoesNotContain("build", output);
    }

    [Fact]
    public void OmitsNullSources()
    {
        var manifest = MakeManifest();
        var output = ManifestSerializer.Serialize(manifest);

        Assert.DoesNotContain("sources", output);
    }

    [Fact]
    public void SerializesMixedDependencies()
    {
        var deps = new PackageDependencies(
            [
                new ZSchemeDependency(
                    "stdlib",
                    new ZSchemeDependencySource.Local("../stdlib"),
                    SourceSpan.None
                ),
            ],
            [new NuGetDependency("System.Collections.Immutable", "9.0.0", SourceSpan.None)]
        );
        var manifest = MakeManifest(deps: deps);
        var output = ManifestSerializer.Serialize(manifest);

        Assert.Contains("(zscheme", output);
        Assert.Contains("(nuget", output);
    }

    [Fact]
    public void SerializesMixedDependencies_RoundTrips()
    {
        var deps = new PackageDependencies(
            [
                new ZSchemeDependency(
                    "stdlib",
                    new ZSchemeDependencySource.Local("../stdlib"),
                    SourceSpan.None
                ),
            ],
            [new NuGetDependency("System.Collections.Immutable", "9.0.0", SourceSpan.None)]
        );
        var manifest = MakeManifest(deps: deps);
        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Dependencies.ZScheme);
        Assert.Single(parsed.Dependencies.NuGet);
    }

    [Fact]
    public void FullManifest_RoundTrips()
    {
        var deps = new PackageDependencies(
            [
                new ZSchemeDependency(
                    "stdlib",
                    new ZSchemeDependencySource.Local("../stdlib"),
                    SourceSpan.None
                ),
            ],
            [new NuGetDependency("System.Collections.Immutable", "9.0.0", SourceSpan.None)]
        );
        var testDeps = new PackageDependencies(
            [
                new ZSchemeDependency(
                    "zunit",
                    new ZSchemeDependencySource.Local("../zunit"),
                    SourceSpan.None
                ),
            ],
            []
        );
        var build = new BuildConfig(new MainBuildConfig(null, null, "ZScheme.MyPkg", []), null);

        var manifest = MakeManifest(
            "zscheme-mypkg",
            "2.0.0",
            "src/main.zs",
            "mypkg",
            "main",
            "My package",
            "Apache-2.0",
            deps,
            testDeps,
            build,
            new SourcePaths("src", "test")
        );

        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.Equal("zscheme-mypkg", parsed!.Name);
        Assert.Equal("2.0.0", parsed.Version);
        Assert.Equal("src/main.zs", parsed.Entry);
        Assert.Equal("mypkg", parsed.ImportPrefix);
        Assert.Equal("main", parsed.DefaultModule);
        Assert.Equal("My package", parsed.Description);
        Assert.Equal("Apache-2.0", parsed.License);
        Assert.Equal("src", parsed.Sources!.Main);
        Assert.Equal("test", parsed.Sources.Test);
        Assert.Single(parsed.Dependencies.ZScheme);
        Assert.Single(parsed.Dependencies.NuGet);
        Assert.Single(parsed.TestDependencies.ZScheme);
        Assert.Equal("ZScheme.MyPkg", parsed.Build.Main!.Namespace);
    }

    [Fact]
    public void RoundTrips_WarnUnusedParams()
    {
        var manifest = new PackageManifest(
            "pkg",
            "1.0.0",
            "main.zs",
            null,
            null,
            null,
            null,
            new PackageDependencies([], []),
            new PackageDependencies([], []),
            new BuildConfig(
                new MainBuildConfig(null, null, null, [], WarnUnusedParameters: false),
                null
            ),
            null,
            SourceSpan.None
        );

        var parsed = RoundTrip(manifest);

        Assert.NotNull(parsed);
        Assert.False(parsed!.Build.Main!.WarnUnusedParameters);
    }
}
