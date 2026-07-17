using System.IO;
using Xunit;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Types;

/// <summary>
///     Exercises <see cref="ZScheme.Compiler.Types.ExhaustivenessValidator" /> through the full
///     compilation pipeline: a <c>match</c> that omits a union case must be rejected at compile
///     time (a <see cref="CompilationResult.ExhaustivenessFailure" />), so codegen never runs on
///     a program that would throw "Non-exhaustive match" at runtime.
/// </summary>
public class ExhaustivenessValidatorTests
{
    private static CompilationResult Compile(string source)
    {
        var options = new CompilerOptions { OutputMode = OutputMode.CSharp, AllowsImplicitModuleName = true };
        return new Compilation(options).Compile(source);
    }

    private static CompilationResult CompileWithStdlib(string source)
    {
        var options = new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
        };
        return new Compilation(options).Compile(source);
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(ExhaustivenessValidatorTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static void AssertExhaustivenessError(CompilationResult result, string missingFragment)
    {
        Assert.IsType<CompilationResult.ExhaustivenessFailure>(result);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains($"Non-exhaustive match: missing cases {missingFragment}")
        );
    }

    // ─── Rejected ─────────────────────────────────────────────────────

    [Fact]
    public void NonExhaustiveUnion_ReportsError()
    {
        var result = Compile(
            @"(module test)
(define-union Shape (Circle [r : Int]) (Rect [w : Int] [h : Int]) (Tri [b : Int] [h : Int]))
(define (area [s : Shape]) : Int
  (match s [(Circle r) (* r r)] [(Rect w h) (* w h)]))
(define (main) : Int (area (Circle 3)))"
        );
        AssertExhaustivenessError(result, "Tri");
    }

    [Fact]
    public void NestedMatchInLet_NonExhaustive_ReportsError()
    {
        // The outer form is a `let`; the non-exhaustive match sits in its body, proving the
        // validator's walk descends into let bodies rather than only checking top-level forms.
        var result = Compile(
            @"(module test)
(define-union U (A [v : Int]) (B [w : Int]))
(define (f [u : U]) : Int
  (let ([k 1]) (match u [(A v) v])))
(define (main) : Int (f (A 2)))"
        );
        AssertExhaustivenessError(result, "B");
    }

    [Fact]
    public void NestedMatchInLambda_NonExhaustive_ReportsError()
    {
        var result = Compile(
            @"(module test)
(define-union U (A [v : Int]) (B [w : Int]))
(define (f [u : U]) : Int
  ((lambda () (match u [(A v) v]))))
(define (main) : Int (f (A 2)))"
        );
        AssertExhaustivenessError(result, "B");
    }

    [Fact]
    public void NestedMatchInMatchArm_NonExhaustive_ReportsError()
    {
        // Outer match is exhaustive (A and B); the inner match on `w` omits B. Proves the walk
        // descends into match-arm bodies.
        var result = Compile(
            @"(module test)
(define-union U (A [v : Int]) (B [w : Int]))
(define (g [u : U] [other : U]) : Int
  (match u
    [(A v) (match other [(A x) x])]
    [(B w) w]))
(define (main) : Int (g (A 1) (A 2)))"
        );
        AssertExhaustivenessError(result, "B");
    }

    [Fact]
    public void CrossModuleOption_MissingNone_ReportsError()
    {
        // The union (Option) is imported from a precompiled package; its case names must reach
        // the checker via ExportedIrDefinitions for this to be caught.
        var result = CompileWithStdlib(
            @"(module test)
(import stdlib/option)
(define (unwrap [o : (Option Int)]) : Int
  (match o [(Some v) v]))
(define (main) : Int (unwrap (Some 5)))"
        );
        AssertExhaustivenessError(result, "None");
    }

    // ─── Accepted ─────────────────────────────────────────────────────

    [Fact]
    public void ExhaustiveUnion_Compiles()
    {
        var result = Compile(
            @"(module test)
(define-union Shape (Circle [r : Int]) (Rect [w : Int] [h : Int]))
(define (area [s : Shape]) : Int
  (match s [(Circle r) (* r r)] [(Rect w h) (* w h)]))
(define (main) : Int (area (Circle 3)))"
        );
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));
    }

    [Fact]
    public void UnionWithTrailingWildcard_Compiles()
    {
        var result = Compile(
            @"(module test)
(define-union Shape (Circle [r : Int]) (Rect [w : Int] [h : Int]) (Tri [b : Int] [h : Int]))
(define (area [s : Shape]) : Int
  (match s [(Circle r) (* r r)] [_ 0]))
(define (main) : Int (area (Circle 3)))"
        );
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Diagnostics));
    }
}
