using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;
// Both namespaces declare DiagnosticSeverity; this file needs Roslyn's only for emit failures.
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

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
    ///     <paramref name="version" /> into <paramref name="dir" />, returning its path. The
    ///     emitted type is <c>{simpleName}.Marker</c>, so it is unique per test yet identical
    ///     across two copies of the same assembly. <paramref name="extraMembers" /> is spliced
    ///     into it, letting a caller emit versions that differ by more than a version number.</summary>
    private static string EmitAssembly(
        string dir,
        string simpleName,
        string version,
        string extraMembers = ""
    )
    {
        Directory.CreateDirectory(dir);
        var source = $$"""
            [assembly: System.Reflection.AssemblyVersion("{{version}}")]
            namespace {{simpleName}}
            {
                public static class Marker
                {
                    public static string Version => "{{version}}";
                    {{extraMembers}}
                }
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
            string.Join("\n", result.Diagnostics.Where(d => d.Severity == RoslynSeverity.Error))
        );
        return path;
    }

    /// <summary>An interface and an implementation of it, spliced into <c>Marker</c> for the
    ///     cross-context assignability test. A <em>derived-to-interface</em> pair is the case no
    ///     name-based fallback can absorb, because the two types do not share a full name.</summary>
    private const string ThingMembers = """
        public interface IThing { }
        public sealed class Thing : IThing { }
        """;

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

    /// <summary>Stands in for the hosting process: loads <paramref name="path" /> into the default
    ///     context, the way OmniSharp's dependencies reach <c>zs-lsp</c> at startup.</summary>
    private static Assembly LoadAsHost(string path)
    {
        var hostCopy = Assembly.LoadFrom(path);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(hostCopy));
        return hostCopy;
    }

    // The scenario this class exists for: the host already carries an assembly of this simple name
    // (zs-lsp ships DependencyInjection.Abstractions 6.0 via OmniSharp) while the compilation is
    // built against a newer copy on its own search path. The early return in EnsureAssemblyLoaded
    // used to test AppDomain.CurrentDomain.GetAssemblies(), which spans every context, so the
    // host's copy suppressed the private load entirely.
    [Fact]
    public void EnsureAssemblyLoaded_StillLoadsPrivately_WhenTheHostCarriesTheSameAssemblyName()
    {
        var hostDir = TempDir();
        var searchDir = TempDir();
        var name = UniqueName();
        try
        {
            EmitAssembly(hostDir, name, "1.0.0.0");
            EmitAssembly(searchDir, name, "2.0.0.0");
            LoadAsHost(Path.Combine(hostDir, name + ".dll"));

            using var interop = new ClrInterop(new DiagnosticBag(), [searchDir]);
            interop.EnsureAssemblyLoaded(name, SourceSpan.None);

            var privateCopy = InteropLoadContext
                .For([searchDir])
                .Assemblies.SingleOrDefault(a => a.GetName().Name == name);

            Assert.NotNull(privateCopy);
            Assert.Equal(new Version(2, 0, 0, 0), privateCopy!.GetName().Version);
        }
        finally
        {
            TryDelete(hostDir);
            TryDelete(searchDir);
        }
    }

    // The other half: loading privately is pointless if lookup still answers first-loaded-wins
    // across every context. FindType must reflect the copy the compilation asked for — here the
    // only one that declares OnlyInV2 — not the older one the host loaded at startup.
    [Fact]
    public void FindType_PrefersThePrivateContext_OverTheHostsCopyOfTheSameAssembly()
    {
        var hostDir = TempDir();
        var searchDir = TempDir();
        var name = UniqueName();
        try
        {
            EmitAssembly(hostDir, name, "1.0.0.0");
            EmitAssembly(searchDir, name, "2.0.0.0", "public static int OnlyInV2 => 2;");
            LoadAsHost(Path.Combine(hostDir, name + ".dll"));

            using var interop = new ClrInterop(new DiagnosticBag(), [searchDir]);
            interop.EnsureAssemblyLoaded(name, SourceSpan.None);

            var type = interop.FindType(name + ".Marker");

            Assert.NotNull(type);
            Assert.Equal(new Version(2, 0, 0, 0), type!.Assembly.GetName().Version);
            Assert.NotNull(type.GetProperty("OnlyInV2"));
        }
        finally
        {
            TryDelete(hostDir);
            TryDelete(searchDir);
        }
    }

    // FindTypeForMember exists to disambiguate same-named types across assemblies by preferring the
    // one that declares the member. That preference must not reach past the private context and
    // pick the host's copy, which is what a bare AppDomain scan does.
    [Fact]
    public void FindTypeForMember_PrefersThePrivateContext_OverTheHostsCopyOfTheSameAssembly()
    {
        var hostDir = TempDir();
        var searchDir = TempDir();
        var name = UniqueName();
        try
        {
            EmitAssembly(hostDir, name, "1.0.0.0", "public static int Shared => 1;");
            EmitAssembly(searchDir, name, "2.0.0.0", "public static int Shared => 2;");
            LoadAsHost(Path.Combine(hostDir, name + ".dll"));

            using var interop = new ClrInterop(new DiagnosticBag(), [searchDir]);
            interop.EnsureAssemblyLoaded(name, SourceSpan.None);

            var type = interop.FindTypeForMember(name + ".Marker", "Shared");

            Assert.NotNull(type);
            Assert.Equal(new Version(2, 0, 0, 0), type!.Assembly.GetName().Version);
        }
        finally
        {
            TryDelete(hostDir);
            TryDelete(searchDir);
        }
    }

    // The split cannot be eliminated. ClrInterop's Resolving handler on the default context has to
    // keep loading there, because that event also services compiled programs executing in-process
    // (PackageTester); routing it into the private context moves the split from compile time to run
    // time and fails the whole aspnet suite with MissingMethodException. So the comparison at the
    // heart of overload matching has to see through it instead: across contexts, both
    // Type.IsAssignableFrom and reference equality are always false, even for the very same file.
    //
    // ArgBindsToParam is the one comparison whose two sides can disagree this way — the argument
    // type comes from FindType (private context first), the parameter type from the context holding
    // its declaring assembly.
    [Theory]
    // The common shape: the same type reached through two contexts.
    [InlineData("Thing", "Thing")]
    // The shape the name-based fallbacks cannot absorb, since the two names differ.
    [InlineData("Thing", "IThing")]
    public void IsClrAssignable_SeesThroughALoadContextSplit(string fromType, string toType)
    {
        var dir = TempDir();
        var name = UniqueName();
        try
        {
            var path = EmitAssembly(dir, name, "1.0.0.0", ThingMembers);

            var hostTo = LoadAsHost(path).GetType($"{name}.Marker+{toType}")!;
            var privateFrom = InteropLoadContext
                .For([dir])
                .LoadFromPath(path)
                .GetType($"{name}.Marker+{fromType}")!;

            // The premise: reflection genuinely cannot relate the two copies.
            Assert.NotSame(hostTo, privateFrom);
            Assert.False(hostTo.IsAssignableFrom(privateFrom));

            Assert.True(ClrInterop.IsClrAssignable(privateFrom, hostTo));
        }
        finally
        {
            TryDelete(dir);
        }
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
