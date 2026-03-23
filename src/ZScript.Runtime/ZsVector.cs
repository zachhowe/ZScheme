namespace ZScript.Runtime;

using System.Collections;
using System.Collections.Immutable;

public sealed class ZsVector<TElement> : IEnumerable<TElement>
{
    private readonly ImmutableArray<TElement> _inner;

    private ZsVector(ImmutableArray<TElement> inner) => _inner = inner;

    public static readonly ZsVector<TElement> Empty = new(ImmutableArray<TElement>.Empty);

    public static ZsVector<TElement> FromItems(params ReadOnlySpan<TElement> items)
    {
        var builder = ImmutableArray.CreateBuilder<TElement>(items.Length);
        foreach (var item in items)
            builder.Add(item);
        return new ZsVector<TElement>(builder.MoveToImmutable());
    }

    public int Count => _inner.Length;

    public TElement this[int index] => _inner[index];

    public ZsVector<TElement> Append(TElement value) => new(_inner.Add(value));

    public ZsVector<TElement> Set(int index, TElement value) => new(_inner.SetItem(index, value));

    public ZsVector<TU> Map<TU>(Func<TElement, TU> f) =>
        new(ImmutableArray.CreateRange(_inner.Select(f)));

    public ZsVector<TElement> Filter(Func<TElement, bool> pred) =>
        new(ImmutableArray.CreateRange(_inner.Where(pred)));

    public TU Fold<TU>(TU seed, Func<TU, TElement, TU> f)
    {
        var acc = seed;
        foreach (var item in _inner) acc = f(acc, item);
        return acc;
    }

    public bool IsEmpty => _inner.Length == 0;

    public IEnumerator<TElement> GetEnumerator() => ((IEnumerable<TElement>)_inner).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"#[{string.Join(", ", _inner)}]";
}

public static class ZsVector
{
    public static ZsVector<T> Of<T>(params T[] items) =>
        ZsVector<T>.FromItems(items);
}
