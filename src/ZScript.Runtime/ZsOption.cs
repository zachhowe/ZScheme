namespace ZScript.Runtime;

public abstract record ZsOption<TValue>
{
    public sealed record Some(TValue Value) : ZsOption<TValue>
    {
        public override string ToString() => $"Some({Value})";
    }

    public sealed record None : ZsOption<TValue>
    {
        public override string ToString() => "None";
    }

    public bool IsSome => this is Some;
    public bool IsNone => this is None;

    public TValue Unwrap() => this is Some s
        ? s.Value
        : throw new InvalidOperationException("Called Unwrap on None");

    public TValue UnwrapOr(TValue defaultValue) => this is Some s ? s.Value : defaultValue;

    public ZsOption<TU> Map<TU>(Func<TValue, TU> f) => this is Some s
        ? new ZsOption<TU>.Some(f(s.Value))
        : new ZsOption<TU>.None();

    public ZsOption<TU> FlatMap<TU>(Func<TValue, ZsOption<TU>> f) => this is Some s
        ? f(s.Value)
        : new ZsOption<TU>.None();
}
