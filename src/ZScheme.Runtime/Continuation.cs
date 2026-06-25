namespace ZScheme.Runtime;

public sealed class Continuation
{
    internal readonly IFrame[] Frames;

    internal Continuation(IFrame[] frames) => Frames = frames;

    public object? Invoke(object? value) => throw new AbortAndResume(Frames, value);

    /// <summary>
    /// Async sibling of <see cref="Invoke"/>. Throws the same <see cref="AbortAndResume"/>;
    /// dispatch to the async resume path is decided by the catching driver
    /// (<see cref="Runtime.RunAsync{T}"/> uses <see cref="Runtime.ResumeAsync"/>; sync
    /// <see cref="Runtime.Run{T}"/> uses <see cref="Runtime.Resume"/>). The two methods exist
    /// only so user code can express intent symmetrically with the surrounding async/sync
    /// context.
    /// </summary>
    public Task<object> InvokeAsync(object? value) => throw new AbortAndResume(Frames, value);
}

internal sealed class AbortAndResume : Exception
{
    public readonly IFrame[] Frames;
    public readonly object? Value;

    public AbortAndResume(IFrame[] frames, object? value)
    {
        Frames = frames;
        Value = value;
    }
}
