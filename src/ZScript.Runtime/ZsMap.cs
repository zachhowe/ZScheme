namespace ZScript.Runtime;

using System.Collections;
using System.Collections.Immutable;

public sealed class ZsMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
{
    private readonly ImmutableDictionary<TKey, TValue> _inner;

    private ZsMap(ImmutableDictionary<TKey, TValue> inner) => _inner = inner;

    public static readonly ZsMap<TKey, TValue> Empty = new(ImmutableDictionary<TKey, TValue>.Empty);

    public static ZsMap<TKey, TValue> FromPairs(params ReadOnlySpan<(TKey Key, TValue Value)> pairs)
    {
        var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>();
        foreach (var (key, value) in pairs)
            builder[key] = value;
        return new ZsMap<TKey, TValue>(builder.ToImmutable());
    }

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("map/count")]
    public int Count => _inner.Count;

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("map/nth", IsIndexer = true)]
    public TValue this[TKey key] => _inner[key];

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("map/get")]
    public ZsOption<TValue> Get(TKey key) => _inner.TryGetValue(key, out var value)
        ? new ZsOption<TValue>.Some(value)
        : new ZsOption<TValue>.None();

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("map/put")]
    public ZsMap<TKey, TValue> Put(TKey key, TValue value) => new(_inner.SetItem(key, value));

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("map/remove")]
    public ZsMap<TKey, TValue> Remove(TKey key) => new(_inner.Remove(key));

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("map/contains-key?")]
    public bool ContainsKey(TKey key) => _inner.ContainsKey(key);

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("map/keys")]
    public ZsList<TKey> Keys => ZsList<TKey>.FromItems(_inner.Keys.ToArray());

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("map/values")]
    public ZsList<TValue> Values => ZsList<TValue>.FromItems(_inner.Values.ToArray());

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("map/empty?")]
    public bool IsEmpty => _inner.Count == 0;

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString()
    {
        var pairs = string.Join(", ", _inner.Select(kv => $"{kv.Key}: {kv.Value}"));
        return $"{{{pairs}}}";
    }
}

public static class ZsMap
{
    public static ZsMap<TKey, TValue> Of<TKey, TValue>(params (TKey Key, TValue Value)[] pairs) where TKey : notnull =>
        ZsMap<TKey, TValue>.FromPairs(pairs);
}
