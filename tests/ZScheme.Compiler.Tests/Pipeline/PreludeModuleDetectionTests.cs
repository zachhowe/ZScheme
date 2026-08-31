using Xunit;
using ZScheme.Compiler.Analysis;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Pipeline;

/// <summary>
///     A prelude module must not be compiled with the prelude injected into it. The prelude is
///     named by package-qualified module names (<c>"stdlib/list"</c>) while the file itself
///     declares the bare one (<c>(module list)</c>), so the two never match and only
///     <see cref="CompilerOptions.PrimaryModuleName" /> — which <c>zs lint</c> and the language
///     server both set — can identify the file.
///     <para>
///         Getting this wrong is invisible to a package build (<c>LibraryCompiler</c> routes every
///         module through <c>CompileAsModule</c>, which never injects a prelude) and shows up only
///         in the tools that read one source file on its own: they saw a wider set of
///         ZScheme-declared type names than the module is ever compiled with.
///     </para>
/// </summary>
public sealed class PreludeModuleDetectionTests
{
    /// <summary>Stands in for <c>stdlib/list</c>: a prelude module whose declaration shadows the
    ///     simple name of a CLR type. <c>StringBuilder</c> rather than the real <c>List</c>
    ///     because the precompiled stdlib is discoverable from the test host, and a name it also
    ///     declares would be in scope no matter which prelude this compilation injects.</summary>
    private const string ShadowSource = """
        (module shadow)
        (export StringBuilder StringBuilder-n)
        (define-record StringBuilder [n : Int])
        """;

    /// <summary>Stands in for <c>stdlib/mutable/treelist</c>: another prelude module, which does
    ///     not import the one above, spelling a member path on the CLR type in full.</summary>
    private const string UserSource = """
        (module user)
        (import-clr
          System.Text
          [sb-append System.Text.StringBuilder.Append
            :instance : (System.Text.StringBuilder String -> System.Text.StringBuilder)])
        """;

    private static CompilerOptions OptionsFor(string dir, string? primaryModuleName)
    {
        return new CompilerOptions
        {
            StopAfterTypeInference = true,
            PreludeModules = ["pre/shadow", "pre/user"],
            PackagePaths = new Dictionary<string, string> { ["pre"] = dir },
            PrimaryModuleName = primaryModuleName,
        };
    }

    private static (Compilation Compilation, IReadOnlyList<Diagnostic> Hints) Analyze(
        string? primaryModuleName
    )
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zs_prelude_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shadow.zs"), ShadowSource);
            var userPath = Path.Combine(dir, "user.zs");
            File.WriteAllText(userPath, UserSource);

            var compilation = new Compilation(OptionsFor(dir, primaryModuleName));
            compilation.Compile(UserSource, userPath);
            Assert.NotNull(compilation.Canonicalizer);

            var hints = new DiagnosticBag();
            new RedundantTypeQualifierAnalyzer(hints).Analyze(
                UserSource,
                userPath,
                compilation.Canonicalizer
            );
            return (
                compilation,
                [.. hints.Diagnostics.Where(d => d.Code == DiagnosticCodes.RedundantTypeQualifier)]
            );
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PreludeModuleNamedByPrimaryModuleName_DoesNotGetThePreludeInjected()
    {
        var (compilation, _) = Analyze("pre/user");

        Assert.DoesNotContain("pre/shadow", compilation.GetCachedModules().Keys);
    }

    [Fact]
    public void NonPreludeModule_StillGetsThePreludeInjected()
    {
        var (compilation, _) = Analyze(null);

        Assert.Contains("pre/shadow", compilation.GetCachedModules().Keys);
    }

    /// <summary>
    ///     The bug this pins: the shadowing declaration lives in a sibling prelude module the file
    ///     never imports, so the module is compiled without a ZScheme <c>StringBuilder</c> in
    ///     scope and the short spelling really does bind to <c>System.Text.StringBuilder</c>.
    ///     ZS0004 has to say so. Before the fix the analyzer read a canonicalizer built from an
    ///     injected prelude and declined on all fourteen of
    ///     <c>packages/stdlib/src/mutable/treelist.zs</c>'s <c>List&lt;T&gt;</c> member paths.
    /// </summary>
    [Fact]
    public void MemberPathShadowedOnlyByAnUnimportedPreludeModule_IsReported()
    {
        var (_, hints) = Analyze("pre/user");

        Assert.NotEmpty(hints);
        Assert.All(hints, h => Assert.Equal(["StringBuilder", "System.Text"], h.Data));
    }

    /// <summary>
    ///     The complement, and the reason the decline exists at all: for a file the prelude really
    ///     does apply to, the short name is the ZScheme record and shortening would change which
    ///     type the member path names.
    /// </summary>
    [Fact]
    public void MemberPathShadowedByAnInjectedPreludeModule_IsNotReported()
    {
        var (_, hints) = Analyze(null);

        Assert.Empty(hints);
    }
}
