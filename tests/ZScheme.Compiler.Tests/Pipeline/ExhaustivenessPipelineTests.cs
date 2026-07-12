using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Pipeline;

/// <summary>End-to-end coverage that the exhaustiveness checker runs as part of type
///     inference, including across module boundaries, and that quick-fix-relevant
///     diagnostics carry their codes and structured data.</summary>
public class ExhaustivenessPipelineTests
{
    private static DiagnosticBag Compile(string source)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                AllowsImplicitModuleName = true,
                StopAfterTypeInference = true,
            }
        );
        compilation.Compile(source, "test.zs");
        return compilation.GetDiagnostics();
    }

    [Fact]
    public void PartialMatchOverLocalUnion_WarnsWithCodeAndData()
    {
        var diag = Compile(
            """
            (module test)
            (define-union Color (Red) (Green) (Blue))
            (define (name [c : Color]) : String
              (match c
                [(Red) "red"]))
            """
        );

        var warning = Assert.Single(
            diag.Diagnostics,
            d => d.Code == DiagnosticCodes.NonExhaustiveMatch
        );
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Green", warning.Message);
        Assert.Contains("Blue", warning.Message);
        Assert.Equal(["Green/0", "Blue/0"], warning.Data);
    }

    [Fact]
    public void PartialMatchWithPayloadCase_ReportsArity()
    {
        var diag = Compile(
            """
            (module test)
            (define-union Shape (Circle [r : Int]) (Rect [w : Int] [h : Int]))
            (define (area [s : Shape]) : Int
              (match s
                [(Circle r) r]))
            """
        );

        var warning = Assert.Single(
            diag.Diagnostics,
            d => d.Code == DiagnosticCodes.NonExhaustiveMatch
        );
        Assert.Equal(["Rect/2"], warning.Data);
    }

    [Fact]
    public void ExhaustiveMatch_NoWarning()
    {
        var diag = Compile(
            """
            (module test)
            (define-union Color (Red) (Green) (Blue))
            (define (name [c : Color]) : String
              (match c
                [(Red) "red"]
                [(Green) "green"]
                [(Blue) "blue"]))
            """
        );

        Assert.DoesNotContain(
            diag.Diagnostics,
            d => d.Code == DiagnosticCodes.NonExhaustiveMatch
        );
    }

    [Fact]
    public void WildcardMatch_NoWarning()
    {
        var diag = Compile(
            """
            (module test)
            (define-union Color (Red) (Green) (Blue))
            (define (name [c : Color]) : String
              (match c
                [(Red) "red"]
                [_ "other"]))
            """
        );

        Assert.DoesNotContain(
            diag.Diagnostics,
            d => d.Code == DiagnosticCodes.NonExhaustiveMatch
        );
    }

    [Fact]
    public void PartialMatchOverImportedUnion_Warns()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_exh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "shapes.zs"),
                """
                (module shapes)
                (define-union Shape (Circle [r : Int]) (Rect [w : Int] [h : Int]))
                (export Shape Circle Rect)
                """
            );

            var mainPath = Path.Combine(dir, "main.zs");
            var mainSource = """
                (module main)
                (import shapes)
                (define (area [s : Shape]) : Int
                  (match s
                    [(Circle r) r]))
                """;
            File.WriteAllText(mainPath, mainSource);

            var compilation = new Compilation(
                new CompilerOptions
                {
                    AllowsImplicitModuleName = true,
                    StopAfterTypeInference = true,
                }
            );
            compilation.Compile(mainSource, mainPath);
            var diag = compilation.GetDiagnostics();

            var warning = Assert.Single(
                diag.Diagnostics,
                d => d.Code == DiagnosticCodes.NonExhaustiveMatch
            );
            Assert.Equal(["Rect/2"], warning.Data);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UndefinedVariable_CarriesCodeAndName()
    {
        var diag = Compile(
            """
            (module test)
            (define (f) some-unknown-name)
            """
        );

        var error = Assert.Single(
            diag.Diagnostics,
            d => d.Code == DiagnosticCodes.UndefinedVariable
        );
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(["some-unknown-name"], error.Data);
    }
}
