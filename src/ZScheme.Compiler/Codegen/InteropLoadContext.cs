using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Serilog;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     The load context the compiler reflects target assemblies through.
///     <para>
///         Reflection must not go through <see cref="AssemblyLoadContext.Default" />: the
///         hosting process has its own assemblies loaded there, and a version clash makes
///         reflection fail outright. The language server hit exactly this — <c>zs-lsp</c>
///         ships <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> 6.0 (pulled
///         in by OmniSharp), so reflecting over a package built against 10.0 threw
///         <see cref="FileNotFoundException" /> from
///         <c>MethodInfo.GetParameters()</c> — the same assembly name was already loaded
///         from a different path, so it could not be loaded again. The <c>zs</c> CLI never
///         saw it because nothing pre-loads a conflicting copy there.
///     </para>
///     <para>
///         Resolution order: assemblies found on the compilation's search paths load here,
///         privately, at the best version those paths carry — which is not always the one the
///         reference asked for, see <see cref="Probe" />. Everything else
///         returns null from <see cref="Load" />, which makes the runtime fall back to the
///         default context — so the BCL (and <c>ZScheme.Runtime</c>, whose types the
///         compiler compares against its own) stays unified and type identity holds.
///     </para>
///     <para>
///         This context governs where assemblies <em>load</em>; <c>ClrInterop.FindInLoadContext</c>
///         is the matching half that makes it authoritative for <em>lookup</em>, by scanning
///         <see cref="AssemblyLoadContext.Assemblies" /> before falling back to <c>Type.GetType</c>
///         and <c>AppDomain.CurrentDomain.GetAssemblies()</c> — both of which see every context and
///         answer first-loaded-wins. Keep the two halves together: loading privately achieves
///         nothing if lookup still finds the host's copy first.
///     </para>
///     <para>
///         <b>The split is permanent, and that is deliberate.</b> Some assemblies genuinely end up
///         in both this context and the default one: <c>IlEmitter.LoadPrecompiledAssembly</c> uses
///         <c>Assembly.LoadFrom</c>, and <c>ClrInterop</c>'s <c>Resolving</c> handler on the default
///         context loads a default-context assembly's dependencies <em>there</em>, not here. Two
///         <see cref="Type" /> objects for the same type from different contexts are never
///         reference-equal and <c>IsAssignableFrom</c> is always false between them, so this looks
///         like something to eliminate. Both routes resist it:
///     </para>
///     <list type="number">
///         <item>
///             Routing the <c>Resolving</c> handler's loads here breaks <em>execution</em>. That
///             event also services compiled programs running in-process — <c>PackageTester</c> runs
///             a package's tests that way, resolving both the pre-loaded main library and each test
///             DLL through it. Binding those to this context, which resolves by newest version on
///             the search paths rather than by what the program was built against, moves the split
///             from compile time to run time: the aspnet suite fails all 32 tests with
///             <see cref="MissingMethodException" /> on <c>TryAddSingleton</c>.
///         </item>
///         <item>
///             Routing <c>LoadPrecompiledAssembly</c> here would give <c>ZScheme.Runtime</c> — which
///             rides the precompiled-assembly list — a private second copy, the very split
///             <see cref="IsSharedWithHost" /> exists to prevent.
///         </item>
///     </list>
///     <para>
///         So the split is absorbed instead of removed: <c>ClrInterop.IsClrAssignable</c> compares
///         type <em>identity</em> (full name plus assembly simple name) rather than
///         <see cref="Type" /> references, which is what keeps overload matching working across it.
///         Anything else that compares reflected types needs the same treatment — a reference
///         comparison there fails silently, and on the <c>:instance</c> path it emits no diagnostic
///         at all (<c>ResolveInstanceOverloadCallSite</c> passes <c>reportAmbiguity: false</c>;
///         <c>SelectOverload</c> logs the rejected candidates and their contexts at debug).
///     </para>
///     <para>
///         Contexts are cached per search-path <em>set</em> — see <see cref="For" /> for why the
///         caller's ordering is not part of the identity — and are never unloaded. The
///         non-collectibility is deliberate: <see cref="Cache" /> is static and holds every context
///         for the life of the process, so <c>isCollectible: true</c> would collect nothing, and the
///         compiler hands <see cref="Assembly" /> and <see cref="Type" /> references out of here into
///         caches that outlive any one compilation. Unloading needs a lifetime design, not a flag.
///         What the set-based key buys is that a long-lived host's accumulation is bounded by how
///         many distinct search-path sets its workspace has, rather than by how many orderings its
///         call sites happen to produce.
///     </para>
/// </summary>
internal sealed class InteropLoadContext : AssemblyLoadContext
{
    private static readonly ILogger Log = Serilog.Log.ForContext<InteropLoadContext>();

    /// <summary>Contexts are cached per search-path set. A fresh context per
    ///     <see cref="ClrInterop" /> would re-load every target assembly on each
    ///     compilation, which the language server does on nearly every edit.</summary>
    private static readonly ConcurrentDictionary<string, InteropLoadContext> Cache = new(
        StringComparer.Ordinal
    );

    /// <summary>Assembly versions are read off disk; the probe runs for every unresolved
    ///     reference, so the answers are memoized process-wide. The key carries the file's write
    ///     time and length as well as its path, so a NuGet restore or a rebuild mid-session
    ///     invalidates the entry instead of pinning a stale version for the life of the host.</summary>
    private static readonly ConcurrentDictionary<string, Version?> VersionCache = new(
        StringComparer.Ordinal
    );

    /// <summary>Several contexts coexist, one per search-path set. They used to share a single
    ///     name, which made <c>ClrInterop.DescribeCandidateForLog</c> — whose whole job is tagging a
    ///     rejected parameter type with the context it came from — unable to tell two private
    ///     contexts apart.</summary>
    private static int _nextId;

    private readonly IReadOnlyList<string> _searchPaths;

    private InteropLoadContext(IReadOnlyList<string> searchPaths)
        : base($"ZSchemeClrInterop#{Interlocked.Increment(ref _nextId)}")
    {
        _searchPaths = searchPaths;
    }

    /// <summary>
    ///     The context for <paramref name="searchPaths" />, created on first use.
    ///     <para>
    ///         The paths are deduplicated and sorted before they become the cache key, so callers
    ///         that assemble the same directories in a different order share one context instead of
    ///         minting one each. They genuinely do differ: <c>AnalysisService</c> appends the NuGet
    ///         directory before the framework directories, <c>PackageBuilder</c> appends it last, and
    ///         <c>PackageAutoInstaller</c> puts it first — and since a context is never unloaded and
    ///         holds its own copy of every assembly it resolves, a language-server process
    ///         accumulated one full set per ordering.
    ///     </para>
    ///     <para>
    ///         The sorted list is also what the context keeps, so <see cref="Probe" /> walks the
    ///         paths in that same order and the resolution is a function of the set alone.
    ///         Conflating orderings would be wrong if caller order were priority, but it is not:
    ///         the probe picks by version, and order breaks only a tie between two copies carrying
    ///         the <em>same</em> version — interchangeable for the signature reflection this context
    ///         exists to serve.
    ///     </para>
    /// </summary>
    public static InteropLoadContext For(IReadOnlyList<string> searchPaths)
    {
        var normalized = Normalize(searchPaths);
        var key = string.Join("\0", normalized);
        return Cache.GetOrAdd(key, _ => new InteropLoadContext(normalized));
    }

    /// <summary>Drops empty entries, collapses duplicates and sorts the rest ordinally. Paths are
    ///     otherwise left as given: callers pass fully-qualified directories, and case-folding them
    ///     would be right on Windows and wrong everywhere else.</summary>
    private static string[] Normalize(IReadOnlyList<string> searchPaths)
    {
        return
        [
            .. searchPaths
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (simpleName is null || IsSharedWithHost(simpleName))
            return null;

        // Already resolved here: reuse it. A context can only hold one assembly per
        // simple name, so handing back a near-enough version beats failing the load —
        // reflection over signatures only needs the type names to line up.
        var loaded = Assemblies.FirstOrDefault(a => a.GetName().Name == simpleName);
        if (loaded is not null)
            return loaded;

        var path = Probe(simpleName, assemblyName.Version);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>
    ///     Loads <paramref name="path" /> into this context. Idempotent for the same path, but
    ///     throws <see cref="FileLoadException" /> for a different path carrying an assembly
    ///     already loaded here under the same identity — a context holds one assembly per name, and
    ///     several search paths can carry the same assembly. Callers probing directories are
    ///     expected to catch and move on.
    /// </summary>
    public Assembly LoadFromPath(string path)
    {
        return LoadFromAssemblyPath(Path.GetFullPath(path));
    }

    /// <summary>
    ///     Resolves by simple name, which routes through <see cref="Load" /> — so the search paths
    ///     are probed first and anything <see cref="IsSharedWithHost" /> covers (or that the probe
    ///     misses) defers to the default context. The <see cref="AssemblyName" /> is built without a
    ///     version, so <see cref="Probe" /> has no version to satisfy and simply takes the newest
    ///     copy it finds.
    /// </summary>
    public Assembly LoadByName(string simpleName)
    {
        return LoadFromAssemblyName(new AssemblyName(simpleName));
    }

    /// <summary>
    ///     Finds the best copy of <paramref name="simpleName" /> on the search paths. Several paths
    ///     can carry the same assembly at different versions (a package's resolved NuGet directory
    ///     and a shared framework directory, say), so an exact match on
    ///     <paramref name="wanted" /> wins outright and otherwise the newest copy does — not
    ///     whichever directory happened to come first. The walk order is <see cref="For" />'s sorted
    ///     one rather than the caller's, so it decides nothing beyond a tie between two copies at
    ///     the same version.
    ///     <para>
    ///         There is deliberately no version floor: when every copy is older than
    ///         <paramref name="wanted" />, the newest of them is still returned rather than nothing.
    ///         The alternative — returning null — hands the bind to the default context, which is
    ///         precisely the host-copy bug this class exists to prevent, so a copy the compilation's
    ///         own search paths carry is the better of the two even below the requested version.
    ///     </para>
    ///     <para>
    ///         What that costs is silence. A custom load context may return any version from
    ///         <see cref="Load" /> and the runtime binds it without complaint — no
    ///         <see cref="FileLoadException" />, no version check — so nothing surfaces at the bind
    ///         itself. Reflection then reports the older shape, and a member added after
    ///         <paramref name="wanted" /> reads back as an ordinary "no such member": on the
    ///         <c>:instance</c> path that is a silently rejected overload with no diagnostic at all.
    ///         The debug log below is the only thread back to the real cause, which is why the
    ///         downgrade is detected here rather than left as a fall-through.
    ///     </para>
    /// </summary>
    private string? Probe(string simpleName, Version? wanted)
    {
        string? best = null;
        Version? bestVersion = null;
        var sawCandidate = false;

        // Normalize drops empty entries, so Directory.Exists is the only guard needed here.
        foreach (var searchPath in _searchPaths)
        {
            if (!Directory.Exists(searchPath))
                continue;

            var candidate = new FileInfo(Path.Combine(searchPath, simpleName + ".dll"));
            if (!candidate.Exists)
                continue;

            var full = candidate.FullName;
            var version = VersionOf(candidate);

            if (wanted is not null && version == wanted)
                return full;

            // An unreadable/native candidate reads back unversioned; it must not displace a real
            // one. Testing `bestVersion is null` here instead would let each successive unversioned
            // candidate overwrite the incumbent, degrading "newest wins" into "last one wins".
            var better = version is not null && (bestVersion is null || version > bestVersion);
            if (!sawCandidate || better)
            {
                best = full;
                bestVersion = version;
                sawCandidate = true;
            }
        }

        // The no-floor policy firing. Reported because the bind that follows will not report it:
        // the runtime accepts the lower version silently, so this is the only record that the
        // shape reflection goes on to see is older than the reference asked for.
        if (best is not null && wanted is not null && (bestVersion is null || bestVersion < wanted))
            Log.Debug(
                "InteropLoadContext.Probe: {SimpleName} was referenced at {WantedVersion}, but no copy on the search paths satisfies it; binding {FoundVersion} from {Path}, so reflection sees the older shape",
                simpleName,
                wanted,
                bestVersion?.ToString() ?? "an unreadable version",
                best
            );

        return best;
    }

    private static Version? VersionOf(FileInfo file)
    {
        var path = file.FullName;
        return VersionCache.GetOrAdd(
            $"{path}\0{file.LastWriteTimeUtc.Ticks}\0{file.Length}",
            _ =>
            {
                try
                {
                    return AssemblyName.GetAssemblyName(path).Version;
                }
                catch
                {
                    // Not a managed assembly, or unreadable — treat as unversioned.
                    return null;
                }
            }
        );
    }

    /// <summary>
    ///     Assemblies that must resolve to the <em>same</em> instance the compiler itself
    ///     uses, so reflected types compare equal to the compiler's own
    ///     <c>typeof(...)</c> references: the BCL, plus the ZScheme runtime contract.
    ///     Loading a private second copy of these would split type identity.
    /// </summary>
    private static bool IsSharedWithHost(string simpleName)
    {
        return simpleName is "netstandard" or "mscorlib" or "System" or "ZScheme.Runtime"
            || simpleName.StartsWith("System.", StringComparison.Ordinal);
    }
}
