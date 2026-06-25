namespace ZScheme.Runtime;

public static class Runtime
{
    /// <summary>
    /// Captures the current continuation by throwing <see cref="SaveContinuation"/>. The throw
    /// propagates up through every wrapped non-tail call site in user code; each wrapper appends
    /// a frame describing its post-call computation. <see cref="Run{T}"/> at program entry catches
    /// the exception and invokes <paramref name="userFn"/> with a reified <see cref="Continuation"/>.
    /// </summary>
    public static object? CallCc(Func<Continuation, object?> userFn)
    {
        throw new SaveContinuation { UserFn = userFn };
    }

    /// <summary>
    /// Typed wrapper for <see cref="CallCc"/>. ZScheme's <c>(call/cc f)</c> compiles to a call
    /// to this method with T=α (the call/cc result type) and U=β (the continuation's nominal
    /// return type — universally polymorphic since invoking a continuation never returns).
    /// </summary>
    public static T CallCcTyped<T, U>(Func<Func<T, U>, T> userFn)
    {
        return (T)
            CallCc(k =>
            {
                Func<T, U> contFn = a => (U)k.Invoke((object?)a)!;
                return (object?)userFn(contFn);
            })!;
    }

    /// <summary>
    /// Installs a delimited-continuation prompt around <paramref name="body"/>. Catches a
    /// <see cref="SaveContinuation"/> tagged with this prompt (thrown by <c>(shift k e)</c>)
    /// and invokes its <see cref="SaveContinuation.ShiftBody"/> with the captured frames.
    /// Untagged or differently-tagged throws bubble past, allowing outer prompts and
    /// <see cref="Run{T}"/> to handle them.
    /// </summary>
    public static T Reset<T>(Func<T> body)
    {
        var tag = new PromptTag();
        PromptStack.Push(tag);
        try
        {
            try
            {
                return body();
            }
            catch (SaveContinuation sc) when (ReferenceEquals(sc.Tag, tag))
            {
                if (sc.ShiftBody is null)
                    throw new InvalidOperationException(
                        "SaveContinuation reached a Reset boundary without a ShiftBody. "
                            + "Did a tagged exception come from somewhere other than ShiftTyped?"
                    );
                var frames = sc.Frames.ToArray();
                return (T)sc.ShiftBody(frames)!;
            }
        }
        finally
        {
            PromptStack.Pop();
        }
    }

    /// <summary>
    /// Captures the continuation up to the dynamically innermost <see cref="Reset{T}"/> by
    /// throwing a <see cref="SaveContinuation"/> tagged with that prompt. The wrapped non-tail
    /// call sites between this throw and the <c>Reset</c> append frames as the exception
    /// propagates; <c>Reset</c> reifies them into a composable <see cref="DelimitedContinuation{TIn, TAns}"/>
    /// and passes it to <paramref name="body"/>.
    /// </summary>
    public static TVal ShiftTyped<TVal, TAns>(Func<Func<TVal, TAns>, TAns> body)
    {
        if (PromptStack.IsEmpty)
            throw new InvalidOperationException(
                "(shift) used outside any (reset). A delimited-continuation capture needs "
                    + "an enclosing prompt; wrap the calling code in (reset ...)."
            );
        var tag = PromptStack.Peek();
        throw new SaveContinuation
        {
            Tag = tag,
            ShiftBody = frames =>
            {
                var k = new DelimitedContinuation<TVal, TAns>(frames);
                return (object?)body(v => k.Invoke(v));
            },
        };
    }

    /// <summary>
    /// Tagged variant of <see cref="Reset{T}"/>. Pushes the user-supplied <paramref name="tag"/>
    /// onto the prompt stack instead of allocating a fresh one, so captures targeting that exact
    /// tag (via <c>(shift tag k …)</c>, <c>(control tag k …)</c>, or <c>(call/comp f tag)</c>)
    /// will land here.
    /// </summary>
    public static T ResetAt<T>(PromptTag tag, Func<T> body)
    {
        PromptStack.Push(tag);
        try
        {
            try
            {
                return body();
            }
            catch (SaveContinuation sc) when (ReferenceEquals(sc.Tag, tag))
            {
                if (sc.ShiftBody is null)
                    throw new InvalidOperationException(
                        "SaveContinuation reached a tagged Reset without a ShiftBody. "
                            + "Did a tagged exception come from somewhere other than ShiftTypedAt/ControlTyped/CallCompTyped?"
                    );
                var frames = sc.Frames.ToArray();
                return (T)sc.ShiftBody(frames)!;
            }
        }
        finally
        {
            PromptStack.Pop();
        }
    }

    /// <summary>
    /// Tagged variant of <see cref="ShiftTyped{TVal,TAns}"/>: throws a <see cref="SaveContinuation"/>
    /// stamped with the user-supplied <paramref name="tag"/>, which propagates past any prompts
    /// with different tags until a matching <see cref="ResetAt{T}"/> consumes it. The captured
    /// continuation reinstalls a fresh prompt of <paramref name="tag"/> on resume (Danvy/Filinski).
    /// </summary>
    public static TVal ShiftTypedAt<TVal, TAns>(PromptTag tag, Func<Func<TVal, TAns>, TAns> body)
    {
        if (!PromptStack.Contains(tag))
            throw new InvalidOperationException(
                "(shift tag …) target prompt-tag is not on the dynamic prompt stack. "
                    + "Wrap the calling code in (prompt tag …) or (reset tag …) before capturing."
            );
        throw new SaveContinuation
        {
            Tag = tag,
            ShiftBody = frames =>
            {
                var k = new TaggedDelimitedContinuation<TVal, TAns>(tag, frames);
                return (object?)body(v => k.Invoke(v));
            },
        };
    }

    /// <summary>
    /// Felleisen <c>(control k body)</c>: like <see cref="ShiftTyped{TVal,TAns}"/>, but the captured
    /// continuation does NOT install a fresh prompt on resume. Targets the dynamically innermost
    /// prompt regardless of tag (the default-tagged shift/reset prompt counts).
    /// </summary>
    public static TVal ControlTyped<TVal, TAns>(Func<Func<TVal, TAns>, TAns> body)
    {
        if (PromptStack.IsEmpty)
            throw new InvalidOperationException(
                "(control) used outside any (prompt) / (reset). A delimited-continuation capture "
                    + "needs an enclosing prompt; wrap the calling code in (prompt …) or (reset …)."
            );
        var tag = PromptStack.Peek();
        throw new SaveContinuation
        {
            Tag = tag,
            ShiftBody = frames =>
            {
                var k = new ComposableContinuation<TVal, TAns>(frames);
                return (object?)body(v => k.Invoke(v));
            },
        };
    }

    /// <summary>
    /// Tagged variant of <see cref="ControlTyped{TVal,TAns}"/>: captures up to the matching tagged
    /// prompt. Captured continuation composes Felleisen-style (no fresh prompt on resume).
    /// </summary>
    public static TVal ControlTypedAt<TVal, TAns>(PromptTag tag, Func<Func<TVal, TAns>, TAns> body)
    {
        if (!PromptStack.Contains(tag))
            throw new InvalidOperationException(
                "(control tag …) target prompt-tag is not on the dynamic prompt stack. "
                    + "Wrap the calling code in (prompt tag …) before capturing."
            );
        throw new SaveContinuation
        {
            Tag = tag,
            ShiftBody = frames =>
            {
                var k = new ComposableContinuation<TVal, TAns>(frames);
                return (object?)body(v => k.Invoke(v));
            },
        };
    }

    /// <summary>
    /// Racket-style <c>call-with-composable-continuation</c>: captures the composable continuation
    /// up to the dynamically innermost prompt and applies <paramref name="userFn"/> to it. The
    /// captured continuation composes Felleisen-style (no fresh prompt on resume), matching
    /// Racket's behavior. Semantically equivalent to <c>(control k (userFn k))</c>.
    /// </summary>
    public static TVal CallCompTyped<TVal, TAns>(Func<Func<TVal, TAns>, TAns> userFn) =>
        ControlTyped<TVal, TAns>(userFn);

    /// <summary>Tagged <c>call/comp</c>; equivalent to <c>(control tag k (userFn k))</c>.</summary>
    public static TVal CallCompTypedAt<TVal, TAns>(
        PromptTag tag,
        Func<Func<TVal, TAns>, TAns> userFn
    ) => ControlTypedAt<TVal, TAns>(tag, userFn);

    /// <summary>Allocates a fresh <see cref="PromptTag"/>. Each call returns a distinct value.</summary>
    public static PromptTag MakePromptTag() => new();

    /// <summary>
    /// Replays a captured frame list, threading <paramref name="initialValue"/> through. Each
    /// frame's invocation is wrapped in try/catch so a re-capture inside the resumption picks
    /// up the still-pending older frames as shared context.
    /// </summary>
    public static object? Resume(IFrame[] frames, object? initialValue)
    {
        object? carry = initialValue;
        for (var i = 0; i < frames.Length; i++)
        {
            try
            {
                carry = frames[i].Invoke(carry!);
            }
            catch (SaveContinuation sce)
            {
                if (i + 1 < frames.Length)
                    sce.AppendSharedContext(frames[(i + 1)..]);
                throw;
            }
        }

        return carry;
    }

    /// <summary>
    /// Async-aware sibling of <see cref="Resume"/>. Awaits each frame's
    /// <see cref="IFrame.InvokeAsync"/> instead of calling sync <see cref="IFrame.Invoke"/>,
    /// so frames whose continuation function is async (their post-call code spans an
    /// <c>await</c>) replay without blocking the dispatch loop. Sync frames stay
    /// allocation-free via the completed <see cref="ValueTask{TResult}"/> default impl.
    /// </summary>
    public static async Task<object?> ResumeAsync(IFrame[] frames, object? initialValue)
    {
        object? carry = initialValue;
        for (var i = 0; i < frames.Length; i++)
        {
            try
            {
                carry = await frames[i].InvokeAsync(carry!);
            }
            catch (SaveContinuation sce)
            {
                if (i + 1 < frames.Length)
                    sce.AppendSharedContext(frames[(i + 1)..]);
                throw;
            }
        }

        return carry;
    }

    /// <summary>
    /// Top-level driver. Runs <paramref name="programMain"/>, catching escaping <see cref="SaveContinuation"/>
    /// (continuation capture) and <c>AbortAndResume</c> (continuation invocation) exceptions and
    /// dispatching them. Loops so a capture inside a resumption is handled correctly.
    /// </summary>
    public static T Run<T>(Func<T> programMain)
    {
        Func<object?> currentTask = () => programMain();
        while (true)
        {
            try
            {
                return (T)currentTask()!;
            }
            catch (SaveContinuation sce)
            {
                var capturedFrames = sce.Frames.ToArray();
                var userFn =
                    sce.UserFn
                    ?? throw new InvalidOperationException(
                        "SaveContinuation escaped without an associated user function — "
                            + "did you throw it directly instead of going through CallCc?"
                    );
                var k = new Continuation(capturedFrames);
                currentTask = () =>
                {
                    var userResult = userFn(k);
                    return Resume(capturedFrames, userResult);
                };
            }
            catch (AbortAndResume ar)
            {
                var frames = ar.Frames;
                var val = ar.Value;
                currentTask = () => Resume(frames, val);
            }
        }
    }

    /// <summary>
    /// Async-aware sibling of <see cref="Run{T}"/>. Used by programs that synthesize at least one
    /// async frame (a continuation function whose post-call code crosses an <c>await</c>). Drives
    /// the same throw/catch loop as <see cref="Run{T}"/> but uses <see cref="ResumeAsync"/> so
    /// async frames are awaited rather than blocked-on.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> programMain)
    {
        Func<Task<object?>> currentTask = async () => (object?)await programMain();
        while (true)
        {
            try
            {
                return (T)(await currentTask())!;
            }
            catch (SaveContinuation sce)
            {
                var capturedFrames = sce.Frames.ToArray();
                var userFn =
                    sce.UserFn
                    ?? throw new InvalidOperationException(
                        "SaveContinuation escaped without an associated user function — "
                            + "did you throw it directly instead of going through CallCc?"
                    );
                var k = new Continuation(capturedFrames);
                currentTask = async () =>
                {
                    var userResult = userFn(k);
                    return await ResumeAsync(capturedFrames, userResult);
                };
            }
            catch (AbortAndResume ar)
            {
                var frames = ar.Frames;
                var val = ar.Value;
                currentTask = () => ResumeAsync(frames, val);
            }
        }
    }
}
