using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ZScheme.Compiler.Tests.Codegen;

/// <summary>
///     Verifies that emitted C# source actually compiles, by feeding it through Roslyn
///     in-process (compile-only — the resulting assembly is never loaded or executed).
///     Mirrors the proven pattern in <c>ZScheme.Fuzzer/Oracles/DifferentialExecOracle.cs</c>,
///     but resolves references from the test host's own dependency closure (the
///     <c>TRUSTED_PLATFORM_ASSEMBLIES</c> set) rather than the bare shared framework. That
///     closure already contains the BCL plus every NuGet dependency of the test project —
///     <c>xunit.*</c>, <c>System.Collections.Immutable</c>, <c>System.Collections.Concurrent</c>,
///     etc. — so emitted code that references those resolves automatically without per-test
///     wiring. Tests that need an additional assembly not in that set (e.g. a freshly built
///     ZScheme package DLL) pass it via <c>extraReferencePaths</c>.
/// </summary>
public static class RoslynCompileVerifier
{
    // The host dependency closure is identical for every test, so resolve it once.
    private static readonly Lazy<IReadOnlyList<MetadataReference>> HostReferences = new(
        LoadHostReferences
    );

    /// <summary>
    ///     Asserts that <paramref name="csSource" /> compiles without any Roslyn errors.
    ///     Only <see cref="DiagnosticSeverity.Error" /> diagnostics fail the test; warnings are ignored.
    /// </summary>
    /// <param name="csSource">The emitted C# source to verify.</param>
    /// <param name="precompiledAssemblyPaths">
    ///     Precompiled module assemblies the emitted code references (from
    ///     <c>CSharpOutputResult.PrecompiledAssemblyPaths</c>); only existing files are added.
    /// </param>
    /// <param name="extraReferencePaths">
    ///     Additional DLL paths to link against, for assemblies not already in the test host's
    ///     dependency closure.
    /// </param>
    public static void AssertCompiles(
        string csSource,
        IReadOnlyList<string>? precompiledAssemblyPaths = null,
        IReadOnlyList<string>? extraReferencePaths = null
    )
    {
        AssertCompiles([csSource], precompiledAssemblyPaths, extraReferencePaths);
    }

    /// <summary>
    ///     Asserts that <paramref name="csSources" /> compile <em>together</em>, as separate
    ///     source files of one assembly. This is how a generated project with one file per
    ///     module is really built, so cross-file references are checked rather than assumed.
    /// </summary>
    public static void AssertCompiles(
        IReadOnlyList<string> csSources,
        IReadOnlyList<string>? precompiledAssemblyPaths = null,
        IReadOnlyList<string>? extraReferencePaths = null
    )
    {
        var references = new List<MetadataReference>(HostReferences.Value);
        foreach (var path in (precompiledAssemblyPaths ?? []).Concat(extraReferencePaths ?? []))
            if (File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));

        var trees = csSources.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable
        );

        var compilation = CSharpCompilation.Create(
            "ZSchemeEmitterVerification",
            trees,
            references,
            options
        );

        var errors = compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        if (errors.Count == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"Emitted C# failed to compile ({errors.Count} error(s)):");
        foreach (var d in errors)
            sb.AppendLine("  " + d);
        for (var i = 0; i < csSources.Count; i++)
        {
            sb.AppendLine(
                csSources.Count == 1
                    ? "--- emitted C# source ---"
                    : $"--- emitted C# source {i + 1}/{csSources.Count} ---"
            );
            AppendNumberedSource(sb, csSources[i]);
        }

        Assert.Fail(sb.ToString());
    }

    private static void AppendNumberedSource(StringBuilder sb, string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            sb.AppendLine($"{i + 1, 4}  {lines[i]}");
    }

    private static IReadOnlyList<MetadataReference> LoadHostReferences()
    {
        // TRUSTED_PLATFORM_ASSEMBLIES is the full set of assemblies the test host runs
        // against: the shared framework plus every NuGet/project dependency copied to the
        // test output. Referencing all of them gives the emitted code the same view the
        // real `dotnet build` of generated C# would have.
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrEmpty(tpa))
            throw new InvalidOperationException(
                "TRUSTED_PLATFORM_ASSEMBLIES is unavailable; cannot resolve reference assemblies."
            );

        return tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }
}
