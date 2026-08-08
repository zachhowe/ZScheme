using System.Reflection;
using Xunit;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Integration;

// A CLR type must mean the same thing whether it is written short (`StringBuilder`) or fully
// qualified (`System.Text.StringBuilder`). A short name resolves only through a namespace
// declared by an `(import-clr Ns ...)` form — including the file's own, which used to be
// invisible to that file's type inference because the hints were not collected until IR
// lowering, a stage later. Both spellings canonicalize to Type.FullName, so they become the
// same ZType and unify. See TypeNameCanonicalizer.
public class ShortClrTypeNameTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(ShortClrTypeNameTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    private static CompilationResult CompileWith(string source, OutputMode mode)
    {
        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = mode,
                AllowsImplicitModuleName = true,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
            }
        );
        return compilation.Compile(source);
    }

    private static string CompileCSharp(string source)
    {
        var result = CompileWith(source, OutputMode.CSharp);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return ((CompilationResult.CSharpOutputResult)result).CsOutput;
    }

    private static Assembly CompileIl(string source)
    {
        var result = CompileWith(source, OutputMode.Il);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );
        return Assembly.Load(((CompilationResult.IlOutputResult)result).OutputBytes);
    }

    private static string InvokeString(Assembly asm, string methodName)
    {
        var method = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        try
        {
            return (string)method.Invoke(null, null)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    /// <summary>
    ///     The parameter is annotated short and the return type fully qualified (and the reverse
    ///     in the second function), so the two spellings must unify in both directions. `new` is
    ///     covered too — it names its type as a bare string rather than a ZType.
    /// </summary>
    private const string MixedSpellings = """
        (module mixed)

        (import-clr
          System.Text
          [sb-append System.Text.StringBuilder.Append
            :instance : (System.Text.StringBuilder String -> System.Text.StringBuilder)]
          [sb-str System.Text.StringBuilder.ToString
            :instance : (StringBuilder -> String)])

        (define (grow [b : StringBuilder]) : System.Text.StringBuilder
          (sb-append b "x"))

        (define (grow2 [b : System.Text.StringBuilder]) : StringBuilder
          (grow b))

        (define (compute) : String
          (sb-str (grow2 (grow (new StringBuilder)))))
        """;

    [Fact]
    public void MixedSpellings_Compile_Il()
    {
        Assert.Equal("xx", InvokeString(CompileIl(MixedSpellings), "Compute"));
    }

    [Fact]
    public void MixedSpellings_Compile_CSharp()
    {
        var cs = CompileCSharp(MixedSpellings);
        // Every occurrence is emitted fully qualified — the short spellings resolved rather than
        // degrading to `object`, which is what the `Name.Contains('.')` guard in TypeMapperCore
        // used to produce for a short CLR name.
        Assert.DoesNotContain("(object b)", cs);
        Assert.Contains("System.Text.StringBuilder Grow(System.Text.StringBuilder b)", cs);
        Assert.Contains("new System.Text.StringBuilder()", cs);
    }

    [Fact]
    public void ShortNameWithoutANamespaceHint_IsStillAnError()
    {
        // Resolution is via import-clr hints only; there is deliberately no blanket assembly
        // scan, which would make two same-named types in different namespaces ambiguous.
        var result = CompileWith(
            """
            (module nohint)

            (import-clr
              [sb-str System.Text.StringBuilder.ToString
                :instance : (System.Text.StringBuilder -> String)])

            (define (compute) : String
              (sb-str (new StringBuilder)))
            """,
            OutputMode.Il
        );
        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics.Diagnostics,
            d => d.Message.Contains("CLR type not found: 'StringBuilder'")
        );
    }

    /// <summary>
    ///     A <c>with-handlers</c> clause names its exception type as a bare string that never
    ///     becomes a ZType, so it needs the same treatment as an annotation — and the IL backend
    ///     resolves that name by reflection with no namespace hints of its own.
    /// </summary>
    [Fact]
    public void WithHandlers_AcceptsAShortExceptionTypeName()
    {
        var source = """
            (module handlers)

            (import-clr System)

            (define (compute) : String
              (with-handlers ([InvalidOperationException _] "caught")
                (raise (new InvalidOperationException "boom"))))
            """;
        Assert.Equal("caught", InvokeString(CompileIl(source), "Compute"));
    }

    /// <summary>
    ///     A ZScheme type keeps its short name even when a hinted namespace happens to contain a
    ///     CLR type with the same simple name — otherwise `Point` here would silently become
    ///     System.Drawing.Point.
    /// </summary>
    [Fact]
    public void ZSchemeTypeIsNotShadowedByASameNamedClrTypeInAHintedNamespace()
    {
        var source = """
            (module shadow)

            (import-clr
              System.Text
              [sb-str System.Text.StringBuilder.ToString
                :instance : (StringBuilder -> String)])

            (define-record StringBuilder [tag : String])

            (define (compute) : String
              (StringBuilder/tag (StringBuilder "mine")))
            """;
        Assert.Equal("mine", InvokeString(CompileIl(source), "Compute"));
    }
}
