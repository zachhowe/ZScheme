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

        return Compile(dir, simpleName, source);
    }

    /// <summary>Emits an assembly that <em>references</em> <paramref name="dependencyPath" /> — a
    ///     method whose parameter type comes from it, so <c>GetParameters()</c> forces the bind.
    ///     This is the only way to drive <c>Probe</c> with a non-null <c>wanted</c>: a reference's
    ///     recorded version is where one comes from, and <c>LoadByName</c> never supplies one.</summary>
    private static void EmitReferencingAssembly(
        string dir,
        string simpleName,
        string dependencyName,
        string dependencyPath
    )
    {
        Compile(
            dir,
            simpleName,
            $$"""
            [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
            public static class Use
            {
                public static void Take({{dependencyName}}.Marker.Thing thing) { }
            }
            """,
            dependencyPath
        );
    }

    private static string Compile(
        string dir,
        string simpleName,
        string source,
        params string[] extraReferences
    )
    {
        Directory.CreateDirectory(dir);
        var compilation = CSharpCompilation.Create(
            simpleName,
            [CSharpSyntaxTree.ParseText(source)],
            ReferenceAssemblies()
                .Concat(
                    extraReferences.Select(p =>
                        (MetadataReference)MetadataReference.CreateFromFile(p)
                    )
                ),
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

    /// <summary>Binds <c>Use.Take</c>'s parameter type through <paramref name="context" />, which is
    ///     what makes the runtime resolve the dependency and so run <c>Probe</c> with a version to
    ///     satisfy.</summary>
    private static Type BindParameterType(InteropLoadContext context, string referrerName)
    {
        var method = context.LoadByName(referrerName).GetType("Use")!.GetMethod("Take")!;
        return method.GetParameters()[0].ParameterType;
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

    /// <summary><paramref name="sortLabel" /> fixes where this directory falls in the probe's walk.
    ///     <c>InteropLoadContext.For</c> sorts its search paths, so a test that needs a particular
    ///     directory probed first cannot get it from argument order — a bare GUID would leave the
    ///     order to chance and quietly stop covering the case it was written for.</summary>
    private static string TempDir(string sortLabel = "") =>
        Path.Combine(Path.GetTempPath(), "zs_ilc_" + sortLabel + Guid.NewGuid().ToString("N"));

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

    // The leak this class's cache key used to cause: AnalysisService, PackageBuilder and
    // PackageAutoInstaller each append the NuGet directory at a different point relative to the
    // framework directories, so one language-server process minted a context per ordering — each
    // holding its own copy of every target assembly, none of them ever unloadable.
    [Fact]
    public void For_ReturnsSameContext_WhenTheSameSearchPathSetArrivesInADifferentOrder()
    {
        var analysisServiceOrder = InteropLoadContext.For(["/nuget", "/fw/a", "/fw/b"]);
        var packageBuilderOrder = InteropLoadContext.For(["/fw/a", "/fw/b", "/nuget"]);
        var autoInstallerOrder = InteropLoadContext.For(["/nuget", "/fw/b", "/fw/a"]);

        Assert.Same(analysisServiceOrder, packageBuilderOrder);
        Assert.Same(analysisServiceOrder, autoInstallerOrder);
    }

    // A framework declared by a package and inherited through its dependency closure resolves to
    // the same directory twice. Call sites dedupe by hand; doing it here too keeps a caller that
    // forgets from minting a second context for an equivalent set.
    [Fact]
    public void For_ReturnsSameContext_WhenSearchPathsRepeatOrAreEmpty()
    {
        var plain = InteropLoadContext.For(["/fw", "/nuget"]);
        var redundant = InteropLoadContext.For(["/nuget", "/fw", "/nuget", "", "/fw"]);

        Assert.Same(plain, redundant);
    }

    [Fact]
    public void For_ReturnsDifferentContexts_ForDifferentSearchPathSets()
    {
        var a = InteropLoadContext.For(["/x/y", "/z"]);
        var b = InteropLoadContext.For(["/x/y", "/z", "/w"]);

        Assert.NotSame(a, b);
    }

    // Each context is named distinctly so ClrInterop.DescribeCandidateForLog — which tags a
    // rejected parameter type with the context it came from — can tell two private contexts apart.
    [Fact]
    public void For_NamesEachContextDistinctly()
    {
        var a = InteropLoadContext.For(["/naming/one"]);
        var b = InteropLoadContext.For(["/naming/two"]);

        Assert.NotNull(a.Name);
        Assert.StartsWith("ZSchemeClrInterop", a.Name!, StringComparison.Ordinal);
        Assert.NotEqual(a.Name, b.Name);
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
    // directory and a shared framework directory, say). The older copy sorts first, so it is probed
    // first, and this fails if the probe simply takes the first hit.
    [Fact]
    public void Probe_PrefersNewestVersion_WhenSeveralSearchPathsCarryTheAssembly()
    {
        var oldDir = TempDir("a_old_");
        var newDir = TempDir("b_new_");
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

    // Versions are memoized process-wide, which used to be keyed on path alone — so a NuGet restore
    // or a rebuild under a long-lived host (zs-lsp) left the probe ranking candidates by versions
    // that no longer existed on disk. The extra directory is only there to make the second lookup a
    // different search-path set, and so a context that has not already resolved this assembly.
    [Fact]
    public void Probe_RanksOnCurrentVersions_AfterAnAssemblyOnASearchPathIsRebuilt()
    {
        var rebuiltDir = TempDir("a_rebuilt_");
        var otherDir = TempDir("b_other_");
        var extraDir = TempDir("d_extra_");
        var name = UniqueName();
        try
        {
            Directory.CreateDirectory(extraDir);
            EmitAssembly(rebuiltDir, name, "1.0.0.0");
            EmitAssembly(otherDir, name, "2.0.0.0");

            var before = InteropLoadContext.For([rebuiltDir, otherDir]).LoadByName(name);
            Assert.Equal(new Version(2, 0, 0, 0), before.GetName().Version);

            // Same path, newer assembly — as a restore or a rebuild would leave it.
            EmitAssembly(rebuiltDir, name, "3.0.0.0");

            var after = InteropLoadContext.For([rebuiltDir, otherDir, extraDir]).LoadByName(name);

            Assert.Equal(new Version(3, 0, 0, 0), after.GetName().Version);
        }
        finally
        {
            TryDelete(rebuiltDir);
            TryDelete(otherDir);
            TryDelete(extraDir);
        }
    }

    // The exact-match branch, which every other test here leaves uncovered: they all go through
    // LoadByName, whose AssemblyName carries no version, so Probe never has one to satisfy. Driving
    // it from a real reference is what distinguishes "the requested version wins" from "the newest
    // wins" — 3.0 is present and would win on recency alone.
    [Fact]
    public void Probe_PrefersTheReferencedVersion_OverANewerCopyOnTheSearchPaths()
    {
        var oldDir = TempDir("a_v1_");
        var wantedDir = TempDir("b_v2_");
        var newestDir = TempDir("c_v3_");
        var referrerDir = TempDir("d_ref_");
        var depName = UniqueName();
        var referrerName = UniqueName();
        try
        {
            EmitAssembly(oldDir, depName, "1.0.0.0", ThingMembers);
            var wanted = EmitAssembly(wantedDir, depName, "2.0.0.0", ThingMembers);
            EmitAssembly(newestDir, depName, "3.0.0.0", ThingMembers);
            EmitReferencingAssembly(referrerDir, referrerName, depName, wanted);

            var context = InteropLoadContext.For([oldDir, wantedDir, newestDir, referrerDir]);

            var parameterType = BindParameterType(context, referrerName);

            Assert.Equal(new Version(2, 0, 0, 0), parameterType.Assembly.GetName().Version);
        }
        finally
        {
            TryDelete(oldDir);
            TryDelete(wantedDir);
            TryDelete(newestDir);
            TryDelete(referrerDir);
        }
    }

    // The no-floor policy, decided deliberately rather than left as a fall-through: with nothing on
    // the search paths satisfying the reference, the newest copy there is bound anyway. Returning
    // null instead would defer to the default context and hand back the host's copy — the very bug
    // this class exists to prevent. The bind is also silent: the runtime accepts a lower version
    // from a custom context without complaint, so GetParameters() succeeding is part of what this
    // pins, and the only report is Probe's debug log.
    [Fact]
    public void Probe_BindsTheNewestCopyBelowTheReference_WhenNoneOnTheSearchPathsSatisfiesIt()
    {
        // Only ever referenced, never searched — this is what makes the reference unsatisfiable.
        var unreachableDir = TempDir("a_v3_");
        var oldDir = TempDir("b_v1_");
        var newestReachableDir = TempDir("c_v2_");
        var referrerDir = TempDir("d_ref_");
        var depName = UniqueName();
        var referrerName = UniqueName();
        try
        {
            EmitAssembly(oldDir, depName, "1.0.0.0", ThingMembers);
            EmitAssembly(newestReachableDir, depName, "2.0.0.0", ThingMembers);
            var unreachable = EmitAssembly(unreachableDir, depName, "3.0.0.0", ThingMembers);
            EmitReferencingAssembly(referrerDir, referrerName, depName, unreachable);

            var context = InteropLoadContext.For([oldDir, newestReachableDir, referrerDir]);

            var parameterType = BindParameterType(context, referrerName);

            Assert.Equal(new Version(2, 0, 0, 0), parameterType.Assembly.GetName().Version);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(parameterType.Assembly));
        }
        finally
        {
            TryDelete(unreachableDir);
            TryDelete(oldDir);
            TryDelete(newestReachableDir);
            TryDelete(referrerDir);
        }
    }

    // A file that is not a managed assembly reads back as "unversioned". It must not become the
    // chosen candidate, and — the regression this pins — it must not reset the incumbent either.
    // The labels put one junk directory on each side of the real one, so the sorted walk covers
    // both: junk before the real copy exercises "does not win", junk after it exercises "does not
    // displace what already won".
    [Fact]
    public void Probe_SkipsUnreadableCandidate_AndStillFindsTheRealAssembly()
    {
        var earlyJunkDir = TempDir("a_junk_");
        var realDir = TempDir("b_real_");
        var lateJunkDir = TempDir("c_junk_");
        var name = UniqueName();
        try
        {
            foreach (var junkDir in (string[])[earlyJunkDir, lateJunkDir])
            {
                Directory.CreateDirectory(junkDir);
                File.WriteAllText(Path.Combine(junkDir, name + ".dll"), "not an assembly");
            }

            EmitAssembly(realDir, name, "3.1.0.0");

            var loaded = InteropLoadContext
                .For([earlyJunkDir, realDir, lateJunkDir])
                .LoadByName(name);

            Assert.Equal(new Version(3, 1, 0, 0), loaded.GetName().Version);
        }
        finally
        {
            TryDelete(earlyJunkDir);
            TryDelete(realDir);
            TryDelete(lateJunkDir);
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
