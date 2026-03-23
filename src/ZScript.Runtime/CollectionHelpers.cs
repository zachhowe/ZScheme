namespace ZScript.Runtime;

using System.Collections.Immutable;
using System.Reflection;

public static class CollectionHelpers
{
    /// <summary>
    /// Reads a named field from an object instance using cached reflection.
    /// Used by the IL emitter to extract union case fields inside generic methods,
    /// working around PersistedAssemblyBuilder's inability to encode MemberRef tokens
    /// on TypeBuilder-defined generic types closed with method-level GenericTypeParameterBuilders.
    /// </summary>
    public static object? GetField(object instance, string fieldName)
    {
        var type = instance.GetType();
        var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is null)
            throw new MissingFieldException($"Field '{fieldName}' not found on type '{type.FullName}'. Available: {string.Join(", ", type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Select(f => f.Name))}");
        return field.GetValue(instance);
    }

    /// <summary>
    /// Structural equality for union case types.
    /// Compares all instance fields by value using reflection.
    /// </summary>
    public static bool UnionCaseEquals(object self, object? other)
    {
        if (other is null) return false;
        var selfType = self.GetType();
        var otherType = other.GetType();
        if (selfType != otherType) return false;

        var fields = selfType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var field in fields)
        {
            var a = field.GetValue(self);
            var b = field.GetValue(other);
            if (!Equals(a, b)) return false;
        }
        return true;
    }

    /// <summary>
    /// Structural hash code for union case types.
    /// Combines hash codes of all instance fields using reflection.
    /// </summary>
    public static int UnionCaseGetHashCode(object self)
    {
        var type = self.GetType();
        var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        var hc = new HashCode();
        hc.Add(type.Name);
        foreach (var field in fields)
            hc.Add(field.GetValue(self));
        return hc.ToHashCode();
    }

    public static ImmutableList<TKey> MapKeys<TKey, TValue>(
        ImmutableDictionary<TKey, TValue> map) where TKey : notnull
        => ImmutableList.CreateRange(map.Keys);

    public static ImmutableList<TValue> MapValues<TKey, TValue>(
        ImmutableDictionary<TKey, TValue> map) where TKey : notnull
        => ImmutableList.CreateRange(map.Values);
}
