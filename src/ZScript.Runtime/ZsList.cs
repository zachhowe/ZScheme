namespace ZScript.Runtime;

using System.Collections;
using System.Collections.Immutable;

public sealed class ZsList<T> : IEnumerable<T>
{
    private readonly ImmutableList<T> _inner;

    private ZsList(ImmutableList<T> inner) => _inner = inner;

    public static readonly ZsList<T> Empty = new(ImmutableList<T>.Empty);

    public static ZsList<T> FromItems(params ReadOnlySpan<T> items)
    {
        var builder = ImmutableList.CreateBuilder<T>();
        foreach (var item in items)
            builder.Add(item);
        return new ZsList<T>(builder.ToImmutable());
    }

    public int Count => _inner.Count;

    public T Head => _inner.Count > 0
        ? _inner[0]
        : throw new InvalidOperationException("Head of empty list");

    public ZsList<T> Tail => _inner.Count > 0
        ? new ZsList<T>(_inner.RemoveAt(0))
        : throw new InvalidOperationException("Tail of empty list");

    public ZsList<T> Cons(T value) => new(_inner.Insert(0, value));

    public ZsList<T> Append(T value) => new(_inner.Add(value));

    public ZsList<T> Concat(ZsList<T> other) => new(_inner.AddRange(other._inner));

    public ZsList<U> Map<U>(Func<T, U> f) =>
        new(ImmutableList.CreateRange(_inner.Select(f)));

    public ZsList<T> Filter(Func<T, bool> pred) =>
        new(ImmutableList.CreateRange(_inner.Where(pred)));

    public U Fold<U>(U seed, Func<U, T, U> f)
    {
        var acc = seed;
        foreach (var item in _inner) acc = f(acc, item);
        return acc;
    }

    public T this[int index] => _inner[index];

    public bool IsEmpty => _inner.Count == 0;

    public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"[{string.Join(", ", _inner)}]";
}

public static class ZsList
{
    public static ZsList<T> Of<T>(params T[] items) =>
        ZsList<T>.FromItems(items);
}
