namespace ZScheme.Runtime;

/// <summary>
/// Thread-local stack of in-flight <see cref="PromptTag"/>s. <see cref="Runtime.Reset{T}"/>
/// pushes a fresh tag for the duration of its body so that any <c>(shift k e)</c> evaluated
/// dynamically beneath it (including in functions called from the body) targets that prompt.
/// Per-thread because delimited control state is local to the call stack.
/// </summary>
public static class PromptStack
{
    [ThreadStatic]
    private static Stack<PromptTag>? _stack;

    private static Stack<PromptTag> Stack => _stack ??= new Stack<PromptTag>();

    public static void Push(PromptTag tag) => Stack.Push(tag);

    public static PromptTag Pop() => Stack.Pop();

    public static PromptTag Peek() => Stack.Peek();

    public static bool IsEmpty => Stack.Count == 0;

    /// <summary>
    /// True iff <paramref name="tag"/> (by reference identity) is anywhere on the current
    /// thread's prompt stack. Tagged capture operators consult this before throwing so a
    /// capture targeting a missing tag fails with a meaningful message instead of escaping
    /// to <see cref="Runtime.Run{T}"/> as an opaque uncaught throw.
    /// </summary>
    public static bool Contains(PromptTag tag)
    {
        foreach (var t in Stack)
            if (ReferenceEquals(t, tag))
                return true;
        return false;
    }
}
