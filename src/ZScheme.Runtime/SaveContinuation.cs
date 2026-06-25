namespace ZScheme.Runtime;

public sealed class SaveContinuation : Exception
{
    public List<IFrame> Frames { get; } = new();

    /// <summary>
    /// Set when this throw originates from <c>(call/cc f)</c>. <see cref="Runtime.Run{T}"/>
    /// catches untagged save-continuations (those with <see cref="Tag"/> = null) and invokes
    /// this delegate with a reified <see cref="Continuation"/>.
    /// </summary>
    public Func<Continuation, object?>? UserFn { get; init; }

    /// <summary>
    /// Non-null when this throw originates from <c>(shift k e)</c>. <see cref="Runtime.Reset{T}"/>
    /// catches save-continuations whose <see cref="Tag"/> matches the prompt it installed and
    /// invokes this delegate with the captured frame list. The delegate wraps those frames in
    /// a <see cref="DelimitedContinuation{TIn, TAns}"/> and runs the user's shift body.
    /// </summary>
    public Func<IFrame[], object?>? ShiftBody { get; init; }

    /// <summary>
    /// Non-null when this throw originates from <c>(shift k e)</c>. Identifies the enclosing
    /// <see cref="Runtime.Reset{T}"/> that should consume this exception. Untagged throws (from
    /// call/cc) propagate past every <c>Reset</c> until they reach <see cref="Runtime.Run{T}"/>.
    /// </summary>
    public PromptTag? Tag { get; init; }

    public void Extend(IFrame frame) => Frames.Add(frame);

    public void AppendSharedContext(IFrame[] olderContext)
    {
        foreach (var f in olderContext)
            if (!Frames.Contains(f))
                Frames.Add(f);
    }
}
