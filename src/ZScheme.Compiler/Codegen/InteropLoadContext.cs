using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

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
///         privately, at the version the compilation actually asked for. Everything else
///         returns null from <see cref="Load" />, which makes the runtime fall back to the
///         default context — so the BCL (and <c>ZScheme.Runtime</c>, whose types the
///         compiler compares against its own) stays unified and type identity holds.
///     </para>
///     <para>
///         <b>Known limitations.</b> This context governs where assemblies <em>load</em>, but
///         <see cref="ClrInterop" />'s lookups still go through <c>Type.GetType</c> and
///         <c>AppDomain.CurrentDomain.GetAssemblies()</c>, which see every context and answer
///         first-loaded-wins. Three consequences, none currently covered by a reproducing test:
///     </para>
///     <list type="number">
///         <item>
///             <c>ClrInterop.EnsureAssemblyLoaded</c> returns early when the host already has an
///             assembly of that simple name loaded, so nothing is loaded here and the host's
///             version is what gets reflected — the case this class exists to prevent.
///         </item>
///         <item>
///             Because assemblies can reach both this context and the default one, two
///             <see cref="Type" /> objects for the same type can coexist. They are never
///             reference-equal and <c>IsAssignableFrom</c> is always false between them, which
///             silently fails overload matching for <c>:instance</c> calls
///             (<c>ResolveInstanceOverloadCallSite</c> passes <c>reportAmbiguity: false</c>).
///         </item>
///         <item>
///             Contexts are cached per <em>ordered</em> search-path list and are not collectible,
///             so callers that build the same paths in different orders get separate contexts,
///             each holding its own copy of every assembly, for the life of the process. Sorting
///             the key would conflate them, which is not safe while <see cref="Probe" /> treats
///             path order as priority.
///         </item>
///     </list>
/// </summary>
internal sealed class InteropLoadContext : AssemblyLoadContext
{
    /// <summary>Contexts are cached per search-path set. A fresh context per
    ///     <see cref="ClrInterop" /> would re-load every target assembly on each
    ///     compilation, which the language server does on nearly every edit.</summary>
    private static readonly ConcurrentDictionary<string, InteropLoadContext> Cache = new(
        StringComparer.Ordinal
    );

    /// <summary>Assembly versions are read off disk; the probe runs for every unresolved
    ///     reference, so the answers are memoized process-wide.</summary>
    private static readonly ConcurrentDictionary<string, Version?> VersionCache = new(
        StringComparer.Ordinal
    );

    private readonly IReadOnlyList<string> _searchPaths;

    private InteropLoadContext(IReadOnlyList<string> searchPaths)
        : base("ZSchemeClrInterop")
    {
        _searchPaths = searchPaths;
    }

    public static InteropLoadContext For(IReadOnlyList<string> searchPaths)
    {
        var key = string.Join("\0", searchPaths);
        return Cache.GetOrAdd(key, _ => new InteropLoadContext([.. searchPaths]));
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
    ///     whichever directory happened to come first.
    ///     <para>
    ///         Note there is no version floor: when every copy is older than
    ///         <paramref name="wanted" />, the newest of them is still returned rather than nothing,
    ///         so the load fails (if it fails) on a version complaint naming a real file instead of
    ///         silently falling back to the host's copy.
    ///     </para>
    /// </summary>
    private string? Probe(string simpleName, Version? wanted)
    {
        string? best = null;
        Version? bestVersion = null;
        var sawCandidate = false;

        foreach (var searchPath in _searchPaths)
        {
            if (string.IsNullOrEmpty(searchPath) || !Directory.Exists(searchPath))
                continue;

            var candidate = Path.Combine(searchPath, simpleName + ".dll");
            if (!File.Exists(candidate))
                continue;

            var full = Path.GetFullPath(candidate);
            var version = VersionOf(full);

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

        return best;
    }

    private static Version? VersionOf(string path)
    {
        return VersionCache.GetOrAdd(
            path,
            p =>
            {
                try
                {
                    return AssemblyName.GetAssemblyName(p).Version;
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
