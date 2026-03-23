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

    public int Count => _inner.Count;

    public TValue this[TKey key] => _inner[key];

    public ZsOption<TValue> Get(TKey key) => _inner.TryGetValue(key, out var value)
        ? new ZsOption<TValue>.Some(value)
        : new ZsOption<TValue>.None();

    public ZsMap<TKey, TValue> Put(TKey key, TValue value) => new(_inner.SetItem(key, value));

    public ZsMap<TKey, TValue> Remove(TKey key) => new(_inner.Remove(key));

    public bool ContainsKey(TKey key) => _inner.ContainsKey(key);

    public ZsList<TKey> Keys => ZsList<TKey>.FromItems(_inner.Keys.ToArray());

    public ZsList<TValue> Values => ZsList<TValue>.FromItems(_inner.Values.ToArray());

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
