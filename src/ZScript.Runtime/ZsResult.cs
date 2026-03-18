namespace ZScript.Runtime;

public abstract record ZsResult<TValue, TError>
{
    public sealed record Ok(TValue Value) : ZsResult<TValue, TError>
    {
        public override string ToString() => $"Ok({Value})";
    }

    public sealed record Err(TError Error) : ZsResult<TValue, TError>
    {
        public override string ToString() => $"Err({Error})";
    }

    public bool IsOk => this is Ok;
    public bool IsErr => this is Err;

    public TValue Unwrap() => this is Ok ok
        ? ok.Value
        : throw new InvalidOperationException("Called Unwrap on Err");

    public ZsResult<TU, TError> Map<TU>(Func<TValue, TU> f) => this is Ok ok
        ? new ZsResult<TU, TError>.Ok(f(ok.Value))
        : new ZsResult<TU, TError>.Err(((Err)this).Error);

    public ZsResult<TU, TError> FlatMap<TU>(Func<TValue, ZsResult<TU, TError>> f) => this is Ok ok
        ? f(ok.Value)
        : new ZsResult<TU, TError>.Err(((Err)this).Error);
}
