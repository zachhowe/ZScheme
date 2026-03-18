namespace ZScript.Runtime;

public sealed record ZsError(string Message, ZsOption<ZsError> Cause)
{
    public ZsError(string message) : this(message, new ZsOption<ZsError>.None()) { }

    public override string ToString() => Cause.IsSome
        ? $"ZsError({Message}, caused by: {Cause.Unwrap()})"
        : $"ZsError({Message})";
}
