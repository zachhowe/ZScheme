using System.Collections.Frozen;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Serilog;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Maps a core-library type onto the assembly a C# compiler expects to find it in.
///     <para>
///         Every corelib type reports <c>System.Private.CoreLib</c> as its assembly at run time,
///         because that is where the implementation lives. Reference assemblies split the same
///         surface across facades — <see cref="string" /> belongs to <c>System.Runtime</c>,
///         <c>Dictionary&lt;,&gt;</c> to <c>System.Collections</c>, <c>Thread</c> to
///         <c>System.Threading.Thread</c> — and that split is the identity a consuming compiler
///         sees. An assembly whose public signatures name <c>System.Private.CoreLib</c> therefore
///         cannot be consumed from C# at all: Roslyn reports <c>CS0012</c> on every such signature
///         and neither referencing the implementation assembly nor aliasing it is a workable
///         answer (it would declare <see cref="object" /> alongside the reference assemblies that
///         only forward it).
///     </para>
///     <para>
///         Redirecting the whole implementation assembly to the module's declared corlib is not
///         the fix either: <c>System.Runtime</c> forwards most of the surface but not all of it,
///         so <c>System.Threading.Thread</c> — which lives behind its own same-named facade —
///         stops loading at run time. The split has to be resolved per type, which is what this
///         map does.
///     </para>
///     <para>
///         The answers come from the shared framework itself. Each facade there carries an
///         <c>ExportedType</c> row per type it forwards into <c>System.Private.CoreLib</c>, and
///         that forwarding table is the mirror image of how the matching reference pack declares
///         the same types — which is precisely the identity the consumer will resolve against.
///         Reading it is a one-off scan of ~170 files (tens of milliseconds), memoized for the
///         life of the process.
///     </para>
/// </summary>
internal static class CorLibFacadeMap
{
    /// <summary>The assembly reflection reports for every corelib type, and the one no reference
    ///     pack contains.</summary>
    public const string ImplementationAssembly = "System.Private.CoreLib";

    private const string CorLibReference = "System.Runtime";

    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(CorLibFacadeMap));

    private static readonly Lazy<FrozenDictionary<string, AssemblyName>> Facades = new(Build);

    /// <summary>
    ///     The reference assembly that owns <paramref name="typeFullName" />, or null when the
    ///     shared framework forwards it from nowhere — an implementation detail that was never
    ///     public API, or a framework layout this scan does not understand. Callers leave those
    ///     pointing at <see cref="ImplementationAssembly" />, which is what they did before this
    ///     map existed and still binds correctly at run time.
    /// </summary>
    public static AssemblyName? FacadeFor(string typeFullName)
    {
        return Facades.Value.GetValueOrDefault(typeFullName);
    }

    /// <summary>
    ///     The type whose full name decides the assembly scope <paramref name="type" /> is emitted
    ///     under: arrays, pointers and by-refs take their element type's scope, a constructed
    ///     generic takes its definition's, and a nested type takes its outermost declaring type's.
    ///     Null for a generic parameter, which has no scope of its own.
    /// </summary>
    public static string? ScopeOwner(Type type)
    {
        while (type.HasElementType)
            type = type.GetElementType()!;
        if (type.IsGenericParameter)
            return null;
        if (type.IsConstructedGenericType)
            type = type.GetGenericTypeDefinition();
        while (type.IsNested)
            type = type.DeclaringType!;
        return type.FullName;
    }

    private static FrozenDictionary<string, AssemblyName> Build()
    {
        var directory = SharedFrameworkDirectory();
        if (directory is null)
        {
            Log.Debug(
                "CorLibFacadeMap: no shared framework directory to scan; corelib references stay on {ImplementationAssembly}",
                ImplementationAssembly
            );
            return FrozenDictionary<string, AssemblyName>.Empty;
        }

        // Type full name -> facade simple name, and the file each facade was read from.
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
        {
            var simpleName = Path.GetFileNameWithoutExtension(file);
            if (IsLegacyAggregate(simpleName))
                continue;

            try
            {
                ScanForwarders(file, simpleName, owners);
                files[simpleName] = file;
            }
            catch (Exception ex)
            {
                // Native or otherwise unreadable: it forwards nothing we can use.
                Log.Debug(ex, "CorLibFacadeMap: skipping {File}", file);
            }
        }

        var identities = new Dictionary<string, AssemblyName?>(StringComparer.Ordinal);
        var map = new Dictionary<string, AssemblyName>(owners.Count, StringComparer.Ordinal);
        foreach (var (typeFullName, facade) in owners)
        {
            if (!identities.TryGetValue(facade, out var identity))
                identities[facade] = identity = IdentityOf(files[facade]);
            if (identity is not null)
                map[typeFullName] = identity;
        }

        Log.Debug(
            "CorLibFacadeMap: {TypeCount} corelib types resolved across {FacadeCount} facades in {Directory}",
            map.Count,
            identities.Values.Count(i => i is not null),
            directory
        );
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The directory the running framework was loaded from, which is the one whose
    ///     layout the emitted assembly must agree with. Empty for a single-file host, where there
    ///     is nothing to scan.</summary>
    private static string? SharedFrameworkDirectory()
    {
        var corLib = typeof(object).Assembly.Location;
        return corLib.Length == 0 ? null : Path.GetDirectoryName(corLib);
    }

    /// <summary>
    ///     Records every type <paramref name="file" /> forwards into
    ///     <see cref="ImplementationAssembly" />. Nested forwards are skipped: their
    ///     <c>Implementation</c> is the declaring exported type rather than an assembly, and only
    ///     the outermost type carries a scope.
    /// </summary>
    private static void ScanForwarders(
        string file,
        string simpleName,
        Dictionary<string, string> owners
    )
    {
        using var stream = File.OpenRead(file);
        using var image = new PEReader(stream);
        if (!image.HasMetadata)
            return;

        var metadata = image.GetMetadataReader();
        foreach (var handle in metadata.ExportedTypes)
        {
            var exported = metadata.GetExportedType(handle);
            if (exported.Implementation.Kind is not HandleKind.AssemblyReference)
                continue;

            var implementation = metadata.GetAssemblyReference(
                (AssemblyReferenceHandle)exported.Implementation
            );
            if (metadata.GetString(implementation.Name) != ImplementationAssembly)
                continue;

            var space = metadata.GetString(exported.Namespace);
            var name = metadata.GetString(exported.Name);
            var fullName = space.Length == 0 ? name : space + "." + name;

            if (!owners.TryGetValue(fullName, out var incumbent) || Prefer(simpleName, incumbent))
                owners[fullName] = simpleName;
        }
    }

    /// <summary>
    ///     Facades that forward the whole corelib surface for compatibility rather than owning any
    ///     of it. Leaving them in would make almost every type ambiguous and let the compatibility
    ///     spelling win over the reference pack's real one.
    /// </summary>
    private static bool IsLegacyAggregate(string simpleName)
    {
        return simpleName is "mscorlib" or "netstandard" or "System" or "System.Core";
    }

    /// <summary>
    ///     Breaks a tie between two facades that both forward the same type. A handful genuinely
    ///     overlap (<c>System.Memory</c> and <c>System.Runtime</c> both forward
    ///     <c>IMemoryOwner&lt;T&gt;</c>, say), and in every observed case the reference pack
    ///     declares the type in <c>System.Runtime</c> and has the other one forward to it — so the
    ///     corlib wins, and anything else falls back to an ordinal pick so the map does not depend
    ///     on directory enumeration order.
    /// </summary>
    private static bool Prefer(string candidate, string incumbent)
    {
        if (incumbent == CorLibReference)
            return false;
        if (candidate == CorLibReference)
            return true;
        return string.CompareOrdinal(candidate, incumbent) < 0;
    }

    /// <summary>Reads a facade's name, version and public key token off disk, without loading it
    ///     into the compiler process.</summary>
    private static AssemblyName? IdentityOf(string file)
    {
        try
        {
            return AssemblyName.GetAssemblyName(file);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "CorLibFacadeMap: could not read the identity of {File}", file);
            return null;
        }
    }
}
