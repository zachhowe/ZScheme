using Serilog;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Types;

/// <summary>
///     Rewrites the <see cref="ZType.ZNamedType.Name" /> of a CLR type to a single canonical
///     spelling — its <see cref="Type.FullName" /> — so that a short name and its fully-qualified
///     counterpart become the <em>same</em> <see cref="ZType" />.
///     <para>
///         <see cref="ZType" /> is a record, so its equality is structural over that raw string,
///         and a type annotation carries whatever the source wrote verbatim
///         (<c>AstBuilder.ParseTypeExpr</c>) while anything reflected out of .NET is named by its
///         full name (<c>ClrInterop.MapClrTypeToZType</c>). Without canonicalization
///         <c>INpcContext</c> and <c>ZWorld.GameServer.NPC.INpcContext</c> are two distinct types
///         that only reconcile through per-site fallbacks, and those fallbacks disagree: the
///         unifier's is skipped for generic types, the IL emitter's covers only interfaces and
///         base classes, and the C# emitter has none because Roslyn resolves `using`s for it.
///     </para>
///     <para>
///         A short name resolves only through a namespace declared by an <c>(import-clr Ns ...)</c>
///         form — in the file itself or in a module it imports — mirroring a C# <c>using</c>.
///         There is deliberately no blanket assembly scan: it would make two same-named types in
///         different namespaces ambiguous.
///     </para>
/// </summary>
public sealed class TypeNameCanonicalizer
{
    private static readonly ILogger Log = Serilog.Log.ForContext<TypeNameCanonicalizer>();

    /// <summary>
    ///     Names that already have exactly one meaning everywhere and must keep their short
    ///     spelling. <c>Object</c>, <c>Task</c> and <c>ValueTuple</c> are matched in both forms by
    ///     <c>Unifier</c>, <c>TypeMapperCore.IsTask</c>/<c>IsValueTuple</c> and
    ///     <c>TypeAliasRegistry</c>, so promoting them would only churn rendered types while
    ///     risking a missed <c>Name == "Task"</c> comparison somewhere.
    /// </summary>
    private static readonly HashSet<string> NeverCanonicalized = new(StringComparer.Ordinal)
    {
        "Object",
        "System.Object",
        "Task",
        "ValueTuple",
        "Clr-Array",
    };

    private readonly IReadOnlyList<string>? _assemblySearchPaths;
    private readonly Dictionary<(string Name, int Arity), string> _cache = new();
    private readonly IReadOnlyList<string> _clrNamespaces;
    private readonly Func<string, bool> _isUserDeclaredType;
    private readonly TypeAliasRegistry? _typeAliases;

    /// <param name="isUserDeclaredType">
    ///     Whether a name denotes a ZScheme-declared record/union/class/interface/alias. Those have
    ///     no CLR namespace until they are emitted, so their short name already <em>is</em>
    ///     canonical and must be left alone — otherwise a namespace hint could accidentally
    ///     resolve them to an unrelated CLR type of the same simple name.
    /// </param>
    public TypeNameCanonicalizer(
        IReadOnlyList<string>? clrNamespaces = null,
        TypeAliasRegistry? typeAliases = null,
        IReadOnlyList<string>? assemblySearchPaths = null,
        Func<string, bool>? isUserDeclaredType = null
    )
    {
        _clrNamespaces = clrNamespaces ?? [];
        _typeAliases = typeAliases;
        _assemblySearchPaths = assemblySearchPaths;
        _isUserDeclaredType = isUserDeclaredType ?? (_ => false);
    }

    /// <summary>
    ///     The canonical spelling of a named type of the given generic arity, or
    ///     <paramref name="name" /> unchanged when it is not a CLR type or cannot be resolved.
    ///     Resolution failure is deliberately silent: the assemblies a compilation can see vary
    ///     (the language server may analyse a package whose references are not built yet), and the
    ///     existing behaviour for an unresolvable name is to leave it alone.
    /// </summary>
    public string Canonical(string name, int arity)
    {
        if (!IsCanonicalizable(name))
            return name;

        var key = (name, arity);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var canonical = Resolve(name, arity) ?? name;
        _cache[key] = canonical;
        if (!ReferenceEquals(canonical, name) && canonical != name)
            Log.Debug("TypeNameCanonicalizer: '{Name}' -> '{Canonical}'", name, canonical);
        return canonical;
    }

    /// <summary>Structurally rewrites every named type inside <paramref name="type" />.</summary>
    public ZType Canonicalize(ZType type)
    {
        switch (type)
        {
            case ZType.ZNamedType nt:
            {
                var name = Canonical(nt.Name, nt.TypeArgs.Count);
                var args = CanonicalizeAll(nt.TypeArgs);
                return name == nt.Name && args is null
                    ? nt
                    : new ZType.ZNamedType(name, args ?? nt.TypeArgs);
            }
            case ZType.ZFuncType ft:
            {
                var ps = CanonicalizeAll(ft.Params);
                var ret = Canonicalize(ft.Return);
                return ps is null && ReferenceEquals(ret, ft.Return)
                    ? ft
                    : new ZType.ZFuncType(ps ?? ft.Params, ret, ft.IsVariadic);
            }
            case ZType.ZNullableType nu:
            {
                var inner = Canonicalize(nu.Inner);
                return ReferenceEquals(inner, nu.Inner) ? nu : new ZType.ZNullableType(inner);
            }
            case ZType.ZForAllType fa:
            {
                var body = Canonicalize(fa.Body);
                return ReferenceEquals(body, fa.Body)
                    ? fa
                    : new ZType.ZForAllType(fa.BoundVars, body);
            }
            // ZDelegateType is deliberately left alone. Its name is a C#-style *closed* generic
            // (`System.Func<int,int>`), which both emitters and ClrTypeNames.ConvertToReflectionTypeName
            // consume as written; rewriting it to a reflection FullName and stripping the arity
            // suffix would erase the type arguments and emit a bare `System.Func`.
            default:
                return type;
        }
    }

    /// <summary>Rewrites a list of type names in place-equivalent fashion (e.g. a class's
    ///     <c>define-class Foo : Base IBar</c> list, which is plain strings, not
    ///     <see cref="ZType" />).</summary>
    public IReadOnlyList<string> CanonicalizeNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return names;
        string[]? rewritten = null;
        for (var i = 0; i < names.Count; i++)
        {
            var canonical = Canonical(names[i], 0);
            if (canonical == names[i])
                continue;
            rewritten ??= names.ToArray();
            rewritten[i] = canonical;
        }

        return rewritten ?? names;
    }

    /// <summary>Null when nothing changed, so callers can keep the original list.</summary>
    private IReadOnlyList<ZType>? CanonicalizeAll(IReadOnlyList<ZType> types)
    {
        ZType[]? rewritten = null;
        for (var i = 0; i < types.Count; i++)
        {
            var canonical = Canonicalize(types[i]);
            if (ReferenceEquals(canonical, types[i]))
                continue;
            rewritten ??= types.ToArray();
            rewritten[i] = canonical;
        }

        return rewritten;
    }

    private bool IsCanonicalizable(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        // Type variables (`^a`) and the single-lowercase-letter type parameters that
        // GeneralizeForExport round-trips are not types to look up.
        if (name[0] == '^' || (name.Length == 1 && char.IsLower(name[0])))
            return false;
        // A C#-style closed generic (`System.Func<int,int>`) resolves to a constructed type whose
        // FullName encodes its arguments after the arity suffix; stripping that suffix would drop
        // them. Such names carry their own arguments already, so there is nothing to canonicalize.
        if (name.Contains('<'))
            return false;
        if (NeverCanonicalized.Contains(name))
            return false;
        if (_typeAliases is not null && _typeAliases.Contains(name))
            return false;
        return !_isUserDeclaredType(name);
    }

    /// <summary>
    ///     Reflection happens only on a cache miss, so the interop instance (and the
    ///     <c>AssemblyLoadContext.Default.Resolving</c> handler it registers) is scoped to the
    ///     lookup rather than kept alive for the compilation. The load contexts it reflects
    ///     through are cached per search-path set, so repeated construction is cheap.
    /// </summary>
    private string? Resolve(string name, int arity)
    {
        using var clr = new ClrInterop(new DiagnosticBag(), _assemblySearchPaths, _typeAliases);
        foreach (var candidate in Candidates(name))
        {
            // A generic type is backed by `Foo`1`; prefer it over a same-named non-generic
            // companion, exactly as ClrInterop.ResolveZLeafToClr does.
            var type =
                arity > 0
                    ? FindType(clr, $"{candidate}`{arity}") ?? FindType(clr, candidate)
                    : FindType(clr, candidate);
            if (type?.FullName is not { } fullName)
                continue;

            // ZScheme keeps generic arity in TypeArgs, so the name never carries the suffix.
            var backtick = fullName.IndexOf('`');
            return backtick >= 0 ? fullName[..backtick] : fullName;
        }

        return null;
    }

    private IEnumerable<string> Candidates(string name)
    {
        yield return name;
        // Only a bare name may be completed by a namespace hint — a name that already spells out
        // a namespace is either right or wrong on its own.
        if (name.Contains('.'))
            yield break;
        foreach (var ns in _clrNamespaces)
            yield return $"{ns}.{name}";
    }

    private static Type? FindType(ClrInterop clr, string name)
    {
        try
        {
            return clr.FindType(name);
        }
        catch (Exception ex)
        {
            // Reflection over a half-built or mismatched assembly set throws; an unresolvable
            // name is not an error here, it just stays as written.
            Log.Debug(ex, "TypeNameCanonicalizer: lookup of '{Name}' failed", name);
            return null;
        }
    }
}
