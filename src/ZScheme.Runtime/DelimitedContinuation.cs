namespace ZScheme.Runtime;

/// <summary>
/// A composable, delimited continuation reified by <c>(shift k e)</c>. Calling
/// <see cref="Invoke"/> runs the captured frames inside a fresh prompt of the same answer
/// type and returns the result normally — unlike <see cref="Continuation"/> (call/cc) which
/// aborts the current computation.
///
/// <para>The fresh prompt around the resumption is what makes the continuation composable:
/// nested <c>(shift ...)</c> evaluated during the resumption is scoped to the new prompt,
/// not the original one (which is already gone by the time we're resuming).</para>
/// </summary>
public sealed class DelimitedContinuation<TIn, TAns>
{
    private readonly IFrame[] _frames;

    internal DelimitedContinuation(IFrame[] frames) => _frames = frames;

    public TAns Invoke(TIn value) =>
        Runtime.Reset<TAns>(() => (TAns)Runtime.Resume(_frames, value)!);

    /// <summary>
    /// Async-aware sibling of <see cref="Invoke"/>. Awaits the captured frames via
    /// <see cref="Runtime.ResumeAsync"/> instead of blocking on them. Used when the captured
    /// continuation crosses an <c>await</c> boundary inside an async function body.
    /// </summary>
    public async Task<TAns> InvokeAsync(TIn value)
    {
        var tag = new PromptTag();
        PromptStack.Push(tag);
        try
        {
            try
            {
                return (TAns)(await Runtime.ResumeAsync(_frames, value!))!;
            }
            catch (SaveContinuation sc) when (ReferenceEquals(sc.Tag, tag))
            {
                if (sc.ShiftBody is null)
                    throw new InvalidOperationException(
                        "SaveContinuation reached a Reset boundary without a ShiftBody."
                    );
                var frames = sc.Frames.ToArray();
                return (TAns)sc.ShiftBody(frames)!;
            }
        }
        finally
        {
            PromptStack.Pop();
        }
    }
}

/// <summary>
/// Tagged variant of <see cref="DelimitedContinuation{TIn, TAns}"/>: on resume installs a fresh
/// prompt with the same tag the original capture targeted, so nested <c>(shift tag …)</c> inside
/// the resumed frames are routed to this re-installed boundary instead of escaping further out.
/// </summary>
public sealed class TaggedDelimitedContinuation<TIn, TAns>
{
    private readonly PromptTag _tag;
    private readonly IFrame[] _frames;

    internal TaggedDelimitedContinuation(PromptTag tag, IFrame[] frames)
    {
        _tag = tag;
        _frames = frames;
    }

    public TAns Invoke(TIn value) =>
        Runtime.ResetAt<TAns>(_tag, () => (TAns)Runtime.Resume(_frames, value)!);

    /// <summary>Async sibling of <see cref="Invoke"/> — see <see cref="DelimitedContinuation{TIn, TAns}.InvokeAsync"/>.</summary>
    public async Task<TAns> InvokeAsync(TIn value)
    {
        PromptStack.Push(_tag);
        try
        {
            try
            {
                return (TAns)(await Runtime.ResumeAsync(_frames, value!))!;
            }
            catch (SaveContinuation sc) when (ReferenceEquals(sc.Tag, _tag))
            {
                if (sc.ShiftBody is null)
                    throw new InvalidOperationException(
                        "SaveContinuation reached a tagged Reset without a ShiftBody."
                    );
                var frames = sc.Frames.ToArray();
                return (TAns)sc.ShiftBody(frames)!;
            }
        }
        finally
        {
            PromptStack.Pop();
        }
    }
}
