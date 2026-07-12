using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

/// <summary>
///     Tests <see cref="PackageTester" /> end-to-end over minimal fixture packages. These are
///     among the slowest unit tests in the suite: the happy paths compile a real library plus
///     one IL test DLL each and run the discovered facts via reflection. Fixture test files
///     avoid the zunit package (whose manifest pulls xunit.v3 from NuGet) by writing the
///     <c>test-case</c> macro's expansion directly — <c>(begin (@ Xunit.FactAttribute)
///     (define (name) ...))</c> — and resolving <c>Xunit.FactAttribute</c> from this test
///     process's own xunit assemblies via <c>additionalAssemblyRefPaths</c>.
/// </summary>
public class PackageTesterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"zs_pkgtester_test_{Guid.NewGuid():N}"
    );

    public PackageTesterTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(PackageTesterTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private const string Manifest = """
        (package
          (name "pt-fixture")
          (version "0.1.0")
          (import-prefix "ptfix")
          (sources (main "src") (test "test")))
        """;

    private const string MainModule = """
        (module main-mod)
        (export add2)
        (define (add2 [x : Int]) : Int (+ x 2))
        """;

    /// <summary>Writes the fixture package and returns the manifest path.</summary>
    private string WriteFixture(
        string manifest = Manifest,
        string? mainModule = MainModule,
        params (string Name, string Source)[] testFiles
    )
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.zspkg"), manifest);
        if (mainModule is not null)
        {
            var srcDir = Path.Combine(_tempDir, "src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "main-mod.zs"), mainModule);
        }

        if (testFiles.Length > 0)
        {
            var testDir = Path.Combine(_tempDir, "test");
            Directory.CreateDirectory(testDir);
            foreach (var (name, source) in testFiles)
                File.WriteAllText(Path.Combine(testDir, name), source);
        }

        return Path.Combine(_tempDir, "package.zspkg");
    }

    private static Task<PackageTestResult?> RunAsync(DiagnosticBag diag, string manifestPath)
    {
        var tester = new PackageTester(diag);
        return tester.TestAsync(
            manifestPath,
            additionalAssemblyRefPaths: [AppContext.BaseDirectory],
            additionalPackagePaths: new Dictionary<string, string>
            {
                ["stdlib"] = GetStdLibPath(),
            }
        );
    }

    [Fact]
    public async Task MissingManifestReturnsNull()
    {
        var diag = new DiagnosticBag();

        var result = await RunAsync(diag, Path.Combine(_tempDir, "package.zspkg"));

        Assert.Null(result);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Manifest not found"));
    }

    [Fact]
    public async Task ManifestWithoutTestSourcesReturnsNull()
    {
        var manifestPath = WriteFixture(
            manifest: """
                (package
                  (name "pt-fixture")
                  (version "0.1.0")
                  (import-prefix "ptfix")
                  (sources (main "src")))
                """
        );
        var diag = new DiagnosticBag();

        var result = await RunAsync(diag, manifestPath);

        Assert.Null(result);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("No test sources defined"));
    }

    [Fact]
    public async Task MissingTestDirectoryReturnsNull()
    {
        var manifestPath = WriteFixture(); // no test files -> test/ never created
        var diag = new DiagnosticBag();

        var result = await RunAsync(diag, manifestPath);

        Assert.Null(result);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("Test directory not found"));
    }

    [Fact]
    public async Task TestDirectoryWithoutZsFilesReturnsNull()
    {
        var manifestPath = WriteFixture();
        Directory.CreateDirectory(Path.Combine(_tempDir, "test"));
        var diag = new DiagnosticBag();

        var result = await RunAsync(diag, manifestPath);

        Assert.Null(result);
        Assert.Contains(diag.Diagnostics, d => d.Message.Contains("No .zs test files found"));
    }

    [Fact]
    public async Task PassingAndFailingFactsAreReported()
    {
        var manifestPath = WriteFixture(
            testFiles: (
                "main-tests.zs",
                """
                (module main-tests)
                (import ptfix/main-mod)
                (begin (@ Xunit.FactAttribute)
                  (define (passing-test) : Int
                    (if (= (add2 40) 42) 1 (raise (new System.Exception "wrong sum")))))
                (begin (@ Xunit.FactAttribute)
                  (define (failing-test) : Int
                    (raise (new System.Exception "boom"))))
                """
            )
        );
        var diag = new DiagnosticBag();

        var result = await RunAsync(diag, manifestPath);

        Assert.NotNull(result);
        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Passed);
        Assert.Equal(1, result.Failed);
        Assert.False(result.Success);
        var failure = Assert.Single(result.Results, r => r.Outcome == TestOutcome.Failed);
        Assert.Contains("boom", failure.FailureMessage);
    }

    [Fact]
    public async Task NonCompilingTestFileIsReportedAsCompilationFailure()
    {
        var manifestPath = WriteFixture(
            testFiles: (
                "broken-tests.zs",
                """
                (module broken-tests)
                (define (oops) : Int "not an int")
                """
            )
        );
        var diag = new DiagnosticBag();

        var result = await RunAsync(diag, manifestPath);

        Assert.NotNull(result);
        var failure = Assert.Single(result.Results);
        Assert.Equal(TestOutcome.Failed, failure.Outcome);
        Assert.EndsWith("(compilation)", failure.TestName);
        Assert.True(diag.HasErrors);
    }

    [Fact]
    public async Task CoverageRequestWritesCoberturaReport()
    {
        var manifestPath = WriteFixture(
            testFiles: (
                "cov-tests.zs",
                """
                (module cov-tests)
                (import ptfix/main-mod)
                (begin (@ Xunit.FactAttribute)
                  (define (covered-test) : Int
                    (if (= (add2 1) 3) 1 (raise (new System.Exception "bad")))))
                """
            )
        );
        var diag = new DiagnosticBag();
        var coveragePath = Path.Combine(_tempDir, "coverage.cobertura.xml");

        var tester = new PackageTester(diag);
        var result = await tester.TestAsync(
            manifestPath,
            additionalAssemblyRefPaths: [AppContext.BaseDirectory],
            additionalPackagePaths: new Dictionary<string, string>
            {
                ["stdlib"] = GetStdLibPath(),
            },
            coverageRequest: new CoverageRequest(
                coveragePath,
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)
            )
        );

        Assert.NotNull(result);
        Assert.True(result.Success, string.Join("\n", diag.Diagnostics));
        Assert.Equal(Path.GetFullPath(coveragePath), result.CoverageOutputPath);
        Assert.True(File.Exists(coveragePath));
        Assert.NotNull(result.Coverage);
        Assert.True(result.Coverage.Value.LinesValid > 0);
    }
}
