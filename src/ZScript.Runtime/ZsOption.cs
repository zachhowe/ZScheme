namespace ZScript.Runtime;

public abstract record ZsOption<T>
{
    public sealed record Some(T Value) : ZsOption<T>
    {
        public override string ToString() => $"Some({Value})";
    }

    public sealed record None() : ZsOption<T>
    {
        public override string ToString() => "None";
    }

    public bool IsSome => this is Some;
    public bool IsNone => this is None;

    public T Unwrap() => this is Some s
        ? s.Value
        : throw new InvalidOperationException("Called Unwrap on None");

    public T UnwrapOr(T defaultValue) => this is Some s ? s.Value : defaultValue;

    public ZsOption<U> Map<U>(Func<T, U> f) => this is Some s
        ? new ZsOption<U>.Some(f(s.Value))
        : new ZsOption<U>.None();

    public ZsOption<U> FlatMap<U>(Func<T, ZsOption<U>> f) => this is Some s
        ? f(s.Value)
        : new ZsOption<U>.None();
}
