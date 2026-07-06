using System.Collections.Concurrent;

namespace ZScheme.Runtime;

/// <summary>
///     A Scheme-style interned symbol. Symbols with the same name are guaranteed to be the
///     same instance process-wide (see <see cref="Intern" />), so reference equality holds and
///     Scheme's <c>(eq? 'a 'a)</c> is <c>#t</c>. Value equality is likewise by name.
/// </summary>
/// <remarks>
///     This type is deliberately <c>sealed</c>, non-abstract, and has no public nested types so
///     the compiler's IL backend treats it purely as an imported external type when loading the
///     shipped <c>ZScheme.Runtime.dll</c> reference.
/// </remarks>
public sealed class ZSymbol : IEquatable<ZSymbol>
{
    private static readonly ConcurrentDictionary<string, ZSymbol> Table = new(StringComparer.Ordinal);

    private ZSymbol(string name)
    {
        Name = name;
    }

    /// <summary>The symbol's textual name (without the leading quote).</summary>
    public string Name { get; }

    /// <summary>
    ///     Returns the canonical interned symbol for <paramref name="name" />. Repeated calls with
    ///     the same name return the identical instance, so symbols can be compared by reference.
    /// </summary>
    public static ZSymbol Intern(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Table.GetOrAdd(name, static n => new ZSymbol(n));
    }

    public bool Equals(ZSymbol? other)
    {
        // Interning makes reference equality sufficient, but fall back to name comparison for
        // robustness against instances that somehow bypass the intern table.
        if (ReferenceEquals(this, other))
            return true;
        return other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is ZSymbol other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Name);
    }

    public static bool operator ==(ZSymbol? left, ZSymbol? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    public static bool operator !=(ZSymbol? left, ZSymbol? right)
    {
        return !(left == right);
    }

    public override string ToString()
    {
        return Name;
    }
}
