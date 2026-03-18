namespace ZScript.Runtime;

public abstract record ZsResult<T, E>
{
    public sealed record Ok(T Value) : ZsResult<T, E>
    {
        public override string ToString() => $"Ok({Value})";
    }

    public sealed record Err(E Error) : ZsResult<T, E>
    {
        public override string ToString() => $"Err({Error})";
    }

    public bool IsOk => this is Ok;
    public bool IsErr => this is Err;

    public T Unwrap() => this is Ok ok
        ? ok.Value
        : throw new InvalidOperationException("Called Unwrap on Err");

    public ZsResult<U, E> Map<U>(Func<T, U> f) => this is Ok ok
        ? new ZsResult<U, E>.Ok(f(ok.Value))
        : new ZsResult<U, E>.Err(((Err)this).Error);

    public ZsResult<U, E> FlatMap<U>(Func<T, ZsResult<U, E>> f) => this is Ok ok
        ? f(ok.Value)
        : new ZsResult<U, E>.Err(((Err)this).Error);
}
