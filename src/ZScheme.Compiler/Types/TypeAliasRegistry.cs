using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Types;

public enum TypeAliasKind
{
    /// <summary>
    ///     The alias resolves to an open-generic CLR type (e.g. System.Collections.Generic.Dictionary`2)
    ///     and is closed over the type arguments at the call site.
    /// </summary>
    GenericClrType,

    /// <summary>
    ///     The alias resolves to a CLR single-dimension array (e.g. T[]).
    ///     Requires exactly one type argument.
    /// </summary>
    SzArray
}

/// <summary>
///     Description of a single ZScheme type alias declared in source via `(define-type-alias ...)`.
/// </summary>
public sealed record TypeAliasInfo(
    string Name,
    IReadOnlyList<string> TypeParams,
    string ClrTarget,
    string? AssemblyHint,
    TypeAliasKind Kind,
    SourceSpan Span);

/// <summary>
///     Compilation-wide registry of type aliases collected from `(define-type-alias ...)` forms
///     across all modules in a single <see cref="ZScheme.Compiler.Pipeline.Compilation"/>. Codegen
///     consults this registry to map ZScheme named types to CLR types.
/// </summary>
public sealed class TypeAliasRegistry
{
    private readonly Dictionary<string, TypeAliasInfo> _aliases = new();

    public IEnumerable<TypeAliasInfo> All => _aliases.Values;

    public bool TryAdd(TypeAliasInfo info, out TypeAliasInfo? existing)
    {
        if (_aliases.TryGetValue(info.Name, out var prev))
        {
            existing = prev;
            return false;
        }
        _aliases[info.Name] = info;
        existing = null;
        return true;
    }

    public bool TryGet(string name, out TypeAliasInfo? info)
    {
        if (_aliases.TryGetValue(name, out var found))
        {
            info = found;
            return true;
        }
        info = null;
        return false;
    }

    public bool Contains(string name)
    {
        return _aliases.ContainsKey(name);
    }
}
