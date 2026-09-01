using System.Reflection;
using System.Runtime.Loader;
using Serilog;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Resolves a precompiled package assembly's references to its <em>sibling</em> package
///     assemblies for as long as the IL emitter is reflecting over them.
///     <para>
///         <c>IlEmitter.LoadPrecompiledAssembly</c> uses <see cref="Assembly.LoadFrom" />,
///         which probes only the app base and the loaded file's own directory. The package cache
///         gives every package its own directory
///         (<c>~/.zscheme/cache/pkg/&lt;compiler&gt;/&lt;pkg&gt;/&lt;version&gt;/</c>), so the
///         moment one package assembly references another — which is the whole point of building a
///         dependency once instead of inlining it — nothing on that probing path can find it.
///         Loading still succeeds, because types resolve lazily; the failure surfaces later, while
///         walking members, as <see cref="FileNotFoundException" /> on the first signature naming a
///         type from the missing assembly (measured: <c>Option LookupScore</c> on a
///         <c>collections.dll</c> separated from <c>zscheme-stdlib.dll</c>).
///     </para>
///     <para>
///         The handler hands the assembly to the context that asked
///         (<see cref="AssemblyLoadContext.LoadFromAssemblyPath" /> on the requesting context)
///         rather than to a private one, for the reason <c>ClrInterop</c>'s resolve handler
///         documents: this event also services compiled programs executing in-process, and binding
///         those to a private context splits type identity at run time. Package assemblies are
///         matched by simple name only — they carry no strong name, and the version a ZScheme
///         package build stamps is always 1.0.0.0.
///     </para>
///     <para>
///         Scoped, not permanent: the subscription lives for one <c>IlEmitter.Emit</c> call,
///         which is the whole window in which that emitter reflects. The assemblies it loads stay
///         loaded — <see cref="AssemblyLoadContext.Default" /> never unloads — so a rebuilt
///         dependency is only picked up by a fresh process.
///     </para>
/// </summary>
public sealed class PrecompiledAssemblyProbe : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PrecompiledAssemblyProbe>();

    private readonly Func<AssemblyLoadContext, AssemblyName, Assembly?>? _handler;

    private PrecompiledAssemblyProbe(IReadOnlyList<string> directories)
    {
        if (directories.Count == 0)
            return;

        _handler = (context, assemblyName) =>
        {
            if (assemblyName.Name is not { } simpleName)
                return null;

            foreach (var directory in directories)
            {
                var candidate = Path.Combine(directory, simpleName + ".dll");
                if (!File.Exists(candidate))
                    continue;
                try
                {
                    var resolved = context.LoadFromAssemblyPath(Path.GetFullPath(candidate));
                    Log.Debug(
                        "PrecompiledAssemblyProbe: resolved {AssemblyName} from {Path}",
                        simpleName,
                        candidate
                    );
                    return resolved;
                }
                catch (Exception ex)
                {
                    Log.Debug(
                        "PrecompiledAssemblyProbe: {AssemblyName} at {Path} failed to load: {Message}",
                        simpleName,
                        candidate,
                        ex.Message
                    );
                }
            }

            return null;
        };

        AssemblyLoadContext.Default.Resolving += _handler;
    }

    /// <summary>
    ///     Probes the directories holding <paramref name="assemblyPaths" />, in the order given and
    ///     without duplicates. Always returns an instance so callers can <c>using</c> it
    ///     unconditionally; one built from an empty set subscribes to nothing.
    /// </summary>
    public static PrecompiledAssemblyProbe For(IReadOnlyList<string>? assemblyPaths)
    {
        if (assemblyPaths is null or { Count: 0 })
            return new PrecompiledAssemblyProbe([]);

        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in assemblyPaths)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is not null && seen.Add(directory) && Directory.Exists(directory))
                directories.Add(directory);
        }

        return new PrecompiledAssemblyProbe(directories);
    }

    public void Dispose()
    {
        if (_handler is not null)
            AssemblyLoadContext.Default.Resolving -= _handler;
    }
}
