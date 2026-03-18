namespace ZScript.Runtime;

using System.Collections;
using System.Collections.Immutable;

public sealed class ZsVector<T> : IEnumerable<T>
{
    private readonly ImmutableArray<T> _inner;

    private ZsVector(ImmutableArray<T> inner) => _inner = inner;

    public static readonly ZsVector<T> Empty = new(ImmutableArray<T>.Empty);

    public static ZsVector<T> FromItems(params ReadOnlySpan<T> items)
    {
        var builder = ImmutableArray.CreateBuilder<T>(items.Length);
        foreach (var item in items)
            builder.Add(item);
        return new ZsVector<T>(builder.MoveToImmutable());
    }

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/count")]
    public int Count => _inner.Length;

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/nth", IsIndexer = true)]
    public T this[int index] => _inner[index];

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/append")]
    public ZsVector<T> Append(T value) => new(_inner.Add(value));

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/set")]
    public ZsVector<T> Set(int index, T value) => new(_inner.SetItem(index, value));

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/map")]
    public ZsVector<U> Map<U>(Func<T, U> f) =>
        new(ImmutableArray.CreateRange(_inner.Select(f)));

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/filter")]
    public ZsVector<T> Filter(Func<T, bool> pred) =>
        new(ImmutableArray.CreateRange(_inner.Where(pred)));

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/fold")]
    public U Fold<U>(U seed, Func<U, T, U> f)
    {
        var acc = seed;
        foreach (var item in _inner) acc = f(acc, item);
        return acc;
    }

    // ReSharper disable once UnusedMember.Global
    [ZsBuiltin("vector/empty?")]
    public bool IsEmpty => _inner.Length == 0;

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_inner).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"#[{string.Join(", ", _inner)}]";
}

public static class ZsVector
{
    public static ZsVector<T> Of<T>(params T[] items) =>
        ZsVector<T>.FromItems(items);
}
