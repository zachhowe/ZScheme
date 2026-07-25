using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZScheme.Compiler.Codegen;

namespace ZScheme.Compiler.Tests.Codegen;

/// <summary>
///     Exercises <see cref="InteropLoadContext" /> with assemblies that really are on a search path.
///     <para>
///         The rest of <c>ClrInteropTests</c> constructs <c>ClrInterop</c> with no search paths, so
///         <c>InteropLoadContext.For([])</c> yields a context whose probe can never find anything and
///         whose <c>Load</c> always returns null — every lookup falls back to the default context and
///         the private-context code is never run. These tests emit throwaway assemblies with Roslyn
///         so the probe has something to find.
///     </para>
///     <para>
///         Loaded assemblies cannot be unloaded and contexts are cached process-wide, so every test
///         uses a unique assembly simple name to stay independent of the others.
///     </para>
/// </summary>
public class InteropLoadContextTests
{
    private static string UniqueName() => "ZsProbeTarget" + Guid.NewGuid().ToString("N");

    /// <summary>Emits a minimal assembly named <paramref name="simpleName" /> at
    ///     <paramref name="version" /> into <paramref name="dir" />, returning its path.</summary>
    private static string EmitAssembly(string dir, string simpleName, string version)
    {
        Directory.CreateDirectory(dir);
        var source = $$"""
            [assembly: System.Reflection.AssemblyVersion("{{version}}")]
            public static class Marker
            {
                public static string Version => "{{version}}";
            }
            """;

        var compilation = CSharpCompilation.Create(
            simpleName,
            [CSharpSyntaxTree.ParseText(source)],
            ReferenceAssemblies(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var path = Path.Combine(dir, simpleName + ".dll");
        var result = compilation.Emit(path);
        Assert.True(
            result.Success,
            string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        );
        return path;
    }

    private static IReadOnlyList<MetadataReference> ReferenceAssemblies()
    {
        var tpa =
            (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)
            ?? throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES unavailable");
        return
        [
            .. tpa.Split(Path.PathSeparator)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Where(File.Exists)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
        ];
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "zs_ilc_" + Guid.NewGuid().ToString("N"));

    /// <summary>Best-effort cleanup. <see cref="InteropLoadContext" /> is not collectible, so once
    ///     an assembly loads the file stays mapped and Windows refuses to delete it for the life of
    ///     the process. Leaving a few KB in the temp directory is preferable to failing the test on
    ///     teardown.</summary>
    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Assembly file still mapped into the process.
        }
    }

    // The language server re-analyses on nearly every edit; a fresh context per compilation would
    // re-load every target assembly each time.
    [Fact]
    public void For_ReturnsSameContext_ForSameSearchPathSet()
    {
        var a = InteropLoadContext.For(["/x/y", "/z"]);
        var b = InteropLoadContext.For(["/x/y", "/z"]);

        Assert.Same(a, b);
    }

    [Fact]
    public void For_ReturnsDifferentContexts_ForDifferentSearchPathSets()
    {
        var a = InteropLoadContext.For(["/x/y", "/z"]);
        var b = InteropLoadContext.For(["/z", "/x/y"]);

        // Documents current behaviour: the cache key is the ordered list, because Probe walks the
        // paths in order and the first exact-version match wins, so order is load-bearing.
        Assert.NotSame(a, b);
    }

    // The whole point of the private context: an assembly the compilation needs must resolve from
    // the search paths, not from whatever the hosting process happens to have loaded.
    [Fact]
    public void LoadByName_ResolvesFromSearchPath_IntoThePrivateContext()
    {
        var dir = TempDir();
        var name = UniqueName();
        try
        {
            EmitAssembly(dir, name, "1.0.0.0");

            var context = InteropLoadContext.For([dir]);
            var loaded = context.LoadByName(name);

            Assert.Equal(name, loaded.GetName().Name);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(loaded));
            Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // Several search paths can carry the same assembly at different versions (a resolved NuGet
    // directory and a shared framework directory, say). The older copy is listed first so this
    // fails if the probe simply takes the first hit.
    [Fact]
    public void Probe_PrefersNewestVersion_WhenSeveralSearchPathsCarryTheAssembly()
    {
        var oldDir = TempDir();
        var newDir = TempDir();
        var name = UniqueName();
        try
        {
            EmitAssembly(oldDir, name, "1.0.0.0");
            EmitAssembly(newDir, name, "2.0.0.0");

            var loaded = InteropLoadContext.For([oldDir, newDir]).LoadByName(name);

            Assert.Equal(new Version(2, 0, 0, 0), loaded.GetName().Version);
        }
        finally
        {
            TryDelete(oldDir);
            TryDelete(newDir);
        }
    }

    // A file that is not a managed assembly reads back as "unversioned". It must not become the
    // chosen candidate, and — the regression this pins — it must not reset the incumbent either.
    [Fact]
    public void Probe_SkipsUnreadableCandidate_AndStillFindsTheRealAssembly()
    {
        var junkDir = TempDir();
        var realDir = TempDir();
        var name = UniqueName();
        try
        {
            Directory.CreateDirectory(junkDir);
            File.WriteAllText(Path.Combine(junkDir, name + ".dll"), "not an assembly");
            EmitAssembly(realDir, name, "3.1.0.0");

            var loaded = InteropLoadContext.For([junkDir, realDir]).LoadByName(name);

            Assert.Equal(new Version(3, 1, 0, 0), loaded.GetName().Version);
        }
        finally
        {
            TryDelete(junkDir);
            TryDelete(realDir);
        }
    }

    // The BCL and ZScheme.Runtime must resolve to the very instance the compiler itself uses,
    // otherwise reflected types stop comparing equal to the compiler's own typeof() references.
    [Theory]
    [InlineData("System.Collections")]
    [InlineData("System.Runtime")]
    [InlineData("ZScheme.Runtime")]
    public void SharedWithHost_ResolvesToTheHostInstance_SoTypeIdentityHolds(string simpleName)
    {
        // A search path that *does* contain these, to prove the sharing rule wins over the probe.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var compilerDir = Path.GetDirectoryName(typeof(InteropLoadContext).Assembly.Location)!;

        var loaded = InteropLoadContext.For([compilerDir, runtimeDir]).LoadByName(simpleName);

        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded));
    }

    // Regression guard for the raw U+0000 that made this file binary to git: the separator must be
    // a NUL character, and distinct path sets must not collide through it.
    [Fact]
    public void For_DoesNotConflateSearchPathSets_ThatDifferOnlyByBoundary()
    {
        var a = InteropLoadContext.For(["ab", "c"]);
        var b = InteropLoadContext.For(["a", "bc"]);

        Assert.NotSame(a, b);
    }
}
