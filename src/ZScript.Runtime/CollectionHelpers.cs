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

    public static ImmutableList<TKey> MapKeys<TKey, TValue>(
        ImmutableDictionary<TKey, TValue> map) where TKey : notnull
        => ImmutableList.CreateRange(map.Keys);

    public static ImmutableList<TValue> MapValues<TKey, TValue>(
        ImmutableDictionary<TKey, TValue> map) where TKey : notnull
        => ImmutableList.CreateRange(map.Values);
}
