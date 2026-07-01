using System.Diagnostics.CodeAnalysis;
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
    SzArray,
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
    SourceSpan Span
);

/// <summary>
///     Compilation-wide registry of type aliases collected from `(define-type-alias ...)` forms
///     across all modules in a single <see cref="ZScheme.Compiler.Pipeline.Compilation" />. Codegen
///     consults this registry to map ZScheme named types to CLR types.
/// </summary>
public sealed class TypeAliasRegistry
{
    private readonly Dictionary<string, TypeAliasInfo> _aliases = new();
    private readonly HashSet<string> _builtInNames = new();

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

    public bool IsBuiltIn(string name)
    {
        return _builtInNames.Contains(name);
    }

    public void RegisterBuiltIn(TypeAliasInfo info)
    {
        _aliases[info.Name] = info;
        _builtInNames.Add(info.Name);
    }

    public bool TryGetZsNameFromClrType(Type clrType, [NotNullWhen(true)] out string? zsName)
    {
        if (clrType.IsArray)
        {
            var elementType = clrType.GetElementType()!;
            foreach (var alias in _aliases.Values)
                if (
                    alias.Kind == TypeAliasKind.SzArray
                    && elementType.GenericTypeArguments.Length == 0
                )
                    // For SzArray aliases with empty ClrTarget (e.g., Mutable-Vector), match any array.
                    // For SzArray aliases with a non-empty ClrTarget, match only arrays whose element type matches.
                    if (
                        string.IsNullOrEmpty(alias.ClrTarget)
                        || elementType.FullName == alias.ClrTarget
                    )
                    {
                        zsName = alias.Name;
                        return true;
                    }
        }

        if (clrType.IsGenericType)
        {
            var genericDef = clrType.GetGenericTypeDefinition();
            var arity = clrType.GetGenericArguments().Length;
            // Strip the backtick arity suffix (e.g., `2) from the CLR type's full name
            // to match against the base type name stored in ClrTarget
            var clrTypeName = genericDef.FullName ?? genericDef.Name;
            var backtickIdx = clrTypeName.IndexOf('`');
            if (backtickIdx >= 0)
                clrTypeName = clrTypeName[..backtickIdx];
            foreach (var alias in _aliases.Values)
            {
                if (alias.Kind != TypeAliasKind.GenericClrType)
                    continue;
                if (clrTypeName == alias.ClrTarget && arity == alias.TypeParams.Count)
                {
                    zsName = alias.Name;
                    return true;
                }
            }
        }

        if (!clrType.IsGenericType)
            foreach (var alias in _aliases.Values)
                if (clrType.FullName == alias.ClrTarget && alias.TypeParams.Count == 0)
                {
                    zsName = alias.Name;
                    return true;
                }

        zsName = null;
        return false;
    }

    public bool TryGetFirstArrayAliasName([NotNullWhen(true)] out string? name)
    {
        // Prefer user-defined array aliases (e.g., Mutable-Vector from stdlib) over built-in ones
        foreach (var alias in _aliases.Values)
            if (alias.Kind == TypeAliasKind.SzArray && !_builtInNames.Contains(alias.Name))
            {
                name = alias.Name;
                return true;
            }

        foreach (var alias in _aliases.Values)
            if (alias.Kind == TypeAliasKind.SzArray)
            {
                name = alias.Name;
                return true;
            }

        name = null;
        return false;
    }

    public bool IsArrayName(string name)
    {
        if (_aliases.TryGetValue(name, out var info))
            return info.Kind == TypeAliasKind.SzArray;
        return false;
    }

    public bool IsTaskName(string name)
    {
        return name is "Task" or "System.Threading.Tasks.Task";
    }

    public bool IsValueTupleName(string name)
    {
        return name == "ValueTuple";
    }
}
