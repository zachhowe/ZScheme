namespace ZScheme.Runtime;

public interface IFrame
{
    // Non-nullable signature (rather than object?) so compiler-synthesized implementations
    // in user assemblies don't trip CS8767 against the interface contract. Null values are
    // passed via `null!` at runtime call sites where needed.
    object Invoke(object returnValue);

    /// <summary>
    /// Async-aware variant used by <see cref="Runtime.ResumeAsync"/>. Sync frames keep the
    /// default implementation (a completed task wrapping <see cref="Invoke"/>); frames whose
    /// continuation function is async — synthesized by ContinuationTransform when a non-tail
    /// call sits inside an async function body — override this to await the underlying
    /// <see cref="Task{TResult}"/> without blocking the replay loop.
    ///
    /// Same non-nullable signature rationale as <see cref="Invoke"/>: keeps emitter-generated
    /// overrides free of CS8613 nullability mismatches.
    /// </summary>
    Task<object> InvokeAsync(object returnValue) => Task.FromResult(Invoke(returnValue));
}
