using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Codegen;

public class TypeAliasCodegenTests
{
    private static string CompileCs(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
            SuppressVersionPreamble = true,
            DisablePrelude = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        var result = compilation.Compile(source);
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Diagnostics));
        return ((CompilationResult.CSharpOutputResult)result).CsOutput;
    }

    private static CompilationResult CompileResult(string source)
    {
        var compilation = new Compilation(new CompilerOptions
        {
            OutputMode = OutputMode.CSharp,
            AllowsImplicitModuleName = true,
            DisablePrelude = true,
            PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() }
        });
        return compilation.Compile(source);
    }

    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(TypeAliasCodegenTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    [Fact]
    public void GenericAlias_RendersAsTargetType()
    {
        // Use a non-hardcoded name so the test exercises the registry path, not the legacy fallback.
        // Pure-annotation form: a pass-through function avoids the CLR-new path
        // (which would re-normalize via ClrInterop.MapClrTypeToZType).
        var cs = CompileCs("""
            (module test)
            (define-type-alias (MyDict ^k ^v) System.Collections.Generic.Dictionary)
            (define (passthru [m : (MyDict String Int)]) : (MyDict String Int) m)
            """);
        Assert.Contains("System.Collections.Generic.Dictionary<string, int>", cs);
    }

    [Fact]
    public void GenericAlias_OneTypeParam_Renders()
    {
        var cs = CompileCs("""
            (module test)
            (define-type-alias (MyList ^a) System.Collections.Generic.List)
            (define (count [xs : (MyList String)]) : Int 0)
            """);
        Assert.Contains("System.Collections.Generic.List<string>", cs);
    }

    [Fact]
    public void ArrayAlias_RendersAsArrayType()
    {
        var cs = CompileCs("""
            (module test)
            (define-type-alias (MyArr ^a) :array)
            (define (first [xs : (MyArr Int)]) : Int 0)
            """);
        Assert.Contains("int[]", cs);
    }

    [Fact]
    public void ArityMismatch_OmitsAliasFallback()
    {
        // Two args declared, one used at the call site.
        var result = CompileResult("""
            (module test)
            (define-type-alias (MyDict ^k ^v) System.Collections.Generic.Dictionary)
            (define (broken [x : (MyDict String)]) : Int 0)
            """);
        // The arity mismatch is not a hard error in alias resolution itself — the type checker
        // should report a problem. Here we just make sure compilation does not crash.
        Assert.NotNull(result);
    }

    [Fact]
    public void DuplicateAlias_DifferentTarget_ReportsError()
    {
        var result = CompileResult("""
            (module test)
            (define-type-alias (MyDict ^k ^v) System.Collections.Generic.Dictionary)
            (define-type-alias (MyDict ^k ^v) System.Collections.Concurrent.ConcurrentDictionary)
            """);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Diagnostics,
            d => d.Message.Contains("already declared with a different target"));
    }

    [Fact]
    public void DuplicateAlias_SameTarget_OK()
    {
        // Two identical declarations are allowed (idempotent — first wins).
        var result = CompileResult("""
            (module test)
            (define-type-alias (MyDict ^k ^v) System.Collections.Generic.Dictionary)
            (define-type-alias (MyDict ^k ^v) System.Collections.Generic.Dictionary)
            (define (passthru [m : (MyDict String Int)]) : (MyDict String Int) m)
            """);
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics.Diagnostics));
    }

    [Fact]
    public void StdlibImport_ResolvesHashAlias()
    {
        // After importing stdlib/hash, the `Hash` alias is in the registry and resolves
        // to ImmutableDictionary in the emitted C#.
        var cs = CompileCs("""
            (module test)
            (import stdlib/hash)
            (define (mk [m : (Hash String Int)]) : (Hash String Int) m)
            """);
        Assert.Contains("System.Collections.Immutable.ImmutableDictionary<string, int>", cs);
    }

    [Fact]
    public void NoStdlibImport_HashDoesNotResolve()
    {
        // Without stdlib/hash imported, the `Hash` name is not registered as an alias.
        // The emitted code falls back to the bare type name (sanitized) — no hardcoded
        // ImmutableDictionary fallback exists in the compiler anymore.
        var cs = CompileCs("""
            (module test)
            (define (mk [m : (Hash String Int)]) : (Hash String Int) m)
            """);
        Assert.DoesNotContain("System.Collections.Immutable.ImmutableDictionary<string, int>", cs);
    }

    [Fact]
    public void Alias_OverridesHardcoded_WhenDeclared()
    {
        // Declaring an alias for a hardcoded name should win — the registry is consulted
        // before the hardcoded switch arms. Use pass-through to avoid the CLR-new
        // type-normalization path.
        var cs = CompileCs("""
            (module test)
            (define-type-alias (Map ^k ^v) System.Collections.Generic.Dictionary)
            (define (passthru [m : (Map String Int)]) : (Map String Int) m)
            """);
        Assert.Contains("System.Collections.Generic.Dictionary<string, int>", cs);
    }
}
