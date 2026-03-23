namespace ZScript.Runtime;

using System.Collections;
using System.Collections.Immutable;

public sealed class ZsList<TElement> : IEnumerable<TElement>
{
    private readonly ImmutableList<TElement> _inner;

    private ZsList(ImmutableList<TElement> inner) => _inner = inner;

    public static readonly ZsList<TElement> Empty = new(ImmutableList<TElement>.Empty);

    public static ZsList<TElement> FromItems(params ReadOnlySpan<TElement> items)
    {
        var builder = ImmutableList.CreateBuilder<TElement>();
        foreach (var item in items)
            builder.Add(item);
        return new ZsList<TElement>(builder.ToImmutable());
    }

    public int Count => _inner.Count;

    public TElement Head => _inner.Count > 0
        ? _inner[0]
        : throw new InvalidOperationException("Head of empty list");

    public ZsList<TElement> Tail => _inner.Count > 0
        ? new ZsList<TElement>(_inner.RemoveAt(0))
        : throw new InvalidOperationException("Tail of empty list");

    public ZsList<TElement> Cons(TElement value) => new(_inner.Insert(0, value));

    public ZsList<TElement> Append(TElement value) => new(_inner.Add(value));

    public ZsList<TElement> Concat(ZsList<TElement> other) => new(_inner.AddRange(other._inner));

    public ZsList<U> Map<U>(Func<TElement, U> f) =>
        new(ImmutableList.CreateRange(_inner.Select(f)));

    public ZsList<TElement> Filter(Func<TElement, bool> pred) =>
        new(ImmutableList.CreateRange(_inner.Where(pred)));

    public TU Fold<TU>(TU seed, Func<TU, TElement, TU> f)
    {
        var acc = seed;
        foreach (var item in _inner) acc = f(acc, item);
        return acc;
    }

    public TElement this[int index] => _inner[index];

    public bool IsEmpty => _inner.Count == 0;

    public IEnumerator<TElement> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"[{string.Join(", ", _inner)}]";
}

public static class ZsList
{
    public static ZsList<T> Of<T>(params T[] items) =>
        ZsList<T>.FromItems(items);
}
