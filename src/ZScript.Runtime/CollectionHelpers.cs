namespace ZScript.Runtime;

using System.Collections.Immutable;

public static class CollectionHelpers
{
    public static ImmutableList<TKey> MapKeys<TKey, TValue>(
        ImmutableDictionary<TKey, TValue> map) where TKey : notnull
        => ImmutableList.CreateRange(map.Keys);

    public static ImmutableList<TValue> MapValues<TKey, TValue>(
        ImmutableDictionary<TKey, TValue> map) where TKey : notnull
        => ImmutableList.CreateRange(map.Values);
}
