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

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/count")]
    public int Count => _inner.Length;

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/nth", IsIndexer = true)]
    public TElement this[int index] => _inner[index];

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/append")]
    public ZsVector<TElement> Append(TElement value) => new(_inner.Add(value));

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/set")]
    public ZsVector<TElement> Set(int index, TElement value) => new(_inner.SetItem(index, value));

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/map")]
    public ZsVector<TU> Map<TU>(Func<TElement, TU> f) =>
        new(ImmutableArray.CreateRange(_inner.Select(f)));

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/filter")]
    public ZsVector<TElement> Filter(Func<TElement, bool> pred) =>
        new(ImmutableArray.CreateRange(_inner.Where(pred)));

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/fold")]
    public TU Fold<TU>(TU seed, Func<TU, TElement, TU> f)
    {
        var acc = seed;
        foreach (var item in _inner) acc = f(acc, item);
        return acc;
    }

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/empty?")]
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
