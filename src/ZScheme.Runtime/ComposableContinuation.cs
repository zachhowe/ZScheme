namespace ZScheme.Runtime;

/// <summary>
/// A composable, delimited continuation reified by <c>(control k e)</c> or
/// <c>(call/comp f)</c>. Calling <see cref="Invoke"/> replays the captured frames in the
/// caller's current dynamic context — unlike <see cref="DelimitedContinuation{TIn, TAns}"/>
/// (shift), which wraps the resumption in a fresh prompt.
///
/// <para>Felleisen-style semantics: a nested <c>(control ...)</c> or <c>(shift ...)</c>
/// inside the resumed frames searches outward through whatever prompt is currently on the
/// dynamic stack, instead of being captured by an artificially reinstated boundary.</para>
/// </summary>
public sealed class ComposableContinuation<TIn, TAns>
{
    private readonly IFrame[] _frames;

    internal ComposableContinuation(IFrame[] frames) => _frames = frames;

    public TAns Invoke(TIn value) => (TAns)Runtime.Resume(_frames, value)!;

    /// <summary>Async sibling of <see cref="Invoke"/>; awaits frames via <see cref="Runtime.ResumeAsync"/>.</summary>
    public async Task<TAns> InvokeAsync(TIn value) =>
        (TAns)(await Runtime.ResumeAsync(_frames, value!))!;
}
