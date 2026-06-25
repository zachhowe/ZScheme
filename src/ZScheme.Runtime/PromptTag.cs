namespace ZScheme.Runtime;

/// <summary>
/// Reference-identity sentinel used to bound a delimited-continuation capture. Each
/// <see cref="Runtime.Reset{T}"/> invocation allocates a fresh tag and pushes it onto the
/// thread-local <see cref="PromptStack"/>. <c>(shift k e)</c> reads the innermost tag and
/// stamps it on the <see cref="SaveContinuation"/> it throws; the matching <c>Reset</c>
/// recognises its own tag via reference equality and consumes the exception.
/// </summary>
public sealed class PromptTag { }
