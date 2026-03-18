namespace ZScript.Runtime;

using System.Collections;
using System.Collections.Immutable;

public sealed class ZsMap<K, V> : IEnumerable<KeyValuePair<K, V>> where K : notnull
{
    private readonly ImmutableDictionary<K, V> _inner;

    private ZsMap(ImmutableDictionary<K, V> inner) => _inner = inner;

    public static readonly ZsMap<K, V> Empty = new(ImmutableDictionary<K, V>.Empty);

    public static ZsMap<K, V> FromPairs(params ReadOnlySpan<(K Key, V Value)> pairs)
    {
        var builder = ImmutableDictionary.CreateBuilder<K, V>();
        foreach (var (key, value) in pairs)
            builder[key] = value;
        return new ZsMap<K, V>(builder.ToImmutable());
    }

    public int Count => _inner.Count;

    public V this[K key] => _inner[key];

    public ZsOption<V> Get(K key) => _inner.TryGetValue(key, out var value)
        ? new ZsOption<V>.Some(value)
        : new ZsOption<V>.None();

    public ZsMap<K, V> Put(K key, V value) => new(_inner.SetItem(key, value));

    public ZsMap<K, V> Remove(K key) => new(_inner.Remove(key));

    public bool ContainsKey(K key) => _inner.ContainsKey(key);

    public ZsList<K> Keys => ZsList<K>.FromItems(_inner.Keys.ToArray());

    public ZsList<V> Values => ZsList<V>.FromItems(_inner.Values.ToArray());

    public bool IsEmpty => _inner.Count == 0;

    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString()
    {
        var pairs = string.Join(", ", _inner.Select(kv => $"{kv.Key}: {kv.Value}"));
        return $"{{{pairs}}}";
    }
}

public static class ZsMap
{
    public static ZsMap<K, V> Of<K, V>(params (K Key, V Value)[] pairs) where K : notnull =>
        ZsMap<K, V>.FromPairs(pairs);
}
