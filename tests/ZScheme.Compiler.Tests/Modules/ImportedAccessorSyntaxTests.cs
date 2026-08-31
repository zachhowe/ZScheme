using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Modules;

// An accessor imported from another module arrives as an overload candidate rather than a plain
// binding, so the deprecated-spelling fallback has to reach it through the overload set. The
// single-file tests in Types/DeprecatedAccessorSyntaxTests.cs never exercise that path.
public class ImportedAccessorSyntaxTests
{
    private const string PkgSource = """
        (module geom)
        (export Point make-point)
        (define-record Point [x : Int] [y : Int])
        (define (make-point [a : Int] [b : Int]) : Point (Point a b))
        """;

    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(ImportedAccessorSyntaxTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    private static CompilationResult CompileMain(string mainSource, bool warn = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_acc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "geom.zs"), PkgSource);
            var mainPath = Path.Combine(dir, "main.zs");
            File.WriteAllText(mainPath, mainSource);

            var compilation = new Compilation(
                new CompilerOptions
                {
                    OutputMode = OutputMode.CSharp,
                    SuppressVersionPreamble = true,
                    DisablePrelude = true,
                    WarnDeprecatedAccessorSyntax = warn,
                    PackagePaths = new Dictionary<string, string>
                    {
                        ["stdlib"] = Path.Combine(RepoRoot(), "packages", "stdlib", "src"),
                    },
                    ModuleSearchPaths = [dir],
                }
            );

            return compilation.Compile(mainSource, mainPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private const string ModernMain = """
        (module main)
        (import geom)
        (define (compute) : Int (+ (Point-x (make-point 3 4)) (Point-y (make-point 3 4))))
        """;

    private const string LegacyMain = """
        (module main)
        (import geom)
        (define (compute) : Int (+ (Point/x (make-point 3 4)) (Point/y (make-point 3 4))))
        """;

    [Fact]
    public void ImportedAccessor_ModernSpelling_Compiles()
    {
        var result = CompileMain(ModernMain);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));
        Assert.DoesNotContain(
            result.Diagnostics.Diagnostics,
            d => d.Code == DiagnosticCodes.DeprecatedAccessorSyntax
        );
    }

    [Fact]
    public void ImportedAccessor_LegacySpelling_StillCompiles_AndWarns()
    {
        var result = CompileMain(LegacyMain);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));

        var warnings = result
            .Diagnostics.Diagnostics.Where(d =>
                d.Code == DiagnosticCodes.DeprecatedAccessorSyntax
            )
            .ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Equal(["Point/x", "Point-x"], warnings[0].Data);
        Assert.Equal(["Point/y", "Point-y"], warnings[1].Data);
    }

    [Fact]
    public void ImportedAccessor_BothSpellings_EmitIdenticalCSharp()
    {
        var modern = (CompilationResult.CSharpOutputResult)CompileMain(ModernMain);
        var legacy = (CompilationResult.CSharpOutputResult)CompileMain(LegacyMain);
        Assert.Equal(modern.CsOutput, legacy.CsOutput);
    }

    [Fact]
    public void ImportedAccessor_LegacySpelling_WarningIsSuppressible()
    {
        var result = CompileMain(LegacyMain, false);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));
        Assert.DoesNotContain(
            result.Diagnostics.Diagnostics,
            d => d.Code == DiagnosticCodes.DeprecatedAccessorSyntax
        );
    }
}
