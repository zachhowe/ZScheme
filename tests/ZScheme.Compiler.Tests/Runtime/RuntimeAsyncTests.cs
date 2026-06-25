using Xunit;
using ZScheme.Runtime;
using RuntimeApi = ZScheme.Runtime.Runtime;

namespace ZScheme.Compiler.Tests.Runtime;

/// <summary>
/// End-to-end tests for the async-aware continuation path: <see cref="RuntimeApi.ResumeAsync"/>
/// and <see cref="RuntimeApi.RunAsync"/>. The IL/C# emitter generates frame classes that
/// implement <see cref="IFrame.InvokeAsync"/> when the captured continuation's tail spans
/// an <c>await</c>; these tests verify the runtime drives that path correctly without
/// blocking on the underlying tasks.
/// </summary>
public class RuntimeAsyncTests
{
    /// <summary>Sync frame — keeps the default <see cref="IFrame.InvokeAsync"/> impl.</summary>
    private sealed class AddOneFrame : IFrame
    {
        public object Invoke(object returnValue) => (int)returnValue + 1;
    }

    /// <summary>Async frame — overrides <see cref="IFrame.InvokeAsync"/> to await an inner task.</summary>
    private sealed class AsyncAddTwoFrame : IFrame
    {
        public object Invoke(object returnValue) =>
            throw new NotSupportedException("Sync Invoke on async frame");

        public async Task<object> InvokeAsync(object returnValue)
        {
            // Simulate yielding to the scheduler before producing a result. Without an
            // async-aware Resume loop this would block.
            await Task.Yield();
            return (int)returnValue + 2;
        }
    }

    [Fact]
    public async Task ResumeAsync_RunsSyncFrame_NoYield()
    {
        var frames = new IFrame[] { new AddOneFrame(), new AddOneFrame() };
        var result = await RuntimeApi.ResumeAsync(frames, 10);
        Assert.Equal(12, result);
    }

    [Fact]
    public async Task ResumeAsync_RunsAsyncFrame_Awaits()
    {
        var frames = new IFrame[] { new AsyncAddTwoFrame() };
        var result = await RuntimeApi.ResumeAsync(frames, 5);
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task ResumeAsync_MixesSyncAndAsyncFrames()
    {
        var frames = new IFrame[]
        {
            new AddOneFrame(), // 5 → 6
            new AsyncAddTwoFrame(), // 6 → 8
            new AddOneFrame(), // 8 → 9
        };
        var result = await RuntimeApi.ResumeAsync(frames, 5);
        Assert.Equal(9, result);
    }

    [Fact]
    public async Task ResumeAsync_PropagatesSaveContinuation_AppendsSharedContext()
    {
        // First frame throws SaveContinuation; ResumeAsync must append the remaining
        // frames as shared context before rethrowing, matching sync Resume's behavior.
        var captured = new SaveContinuation();
        var thrower = new ThrowingFrame(captured);
        var tail1 = new AddOneFrame();
        var tail2 = new AddOneFrame();
        var frames = new IFrame[] { thrower, tail1, tail2 };

        var ex = await Assert.ThrowsAsync<SaveContinuation>(async () =>
            await RuntimeApi.ResumeAsync(frames, 0)
        );
        Assert.Same(captured, ex);
        // Two tail frames should be appended as shared context.
        Assert.Equal(2, ex.Frames.Count);
        Assert.Same(tail1, ex.Frames[0]);
        Assert.Same(tail2, ex.Frames[1]);
    }

    [Fact]
    public async Task RunAsync_NormalReturn_ReplaysFrames()
    {
        // Mirror RuntimeSmokeTests.CallCc_NormalReturn_ReplaysFrames in async form.
        var result = await RuntimeApi.RunAsync(async () =>
        {
            object? x;
            try
            {
                x = RuntimeApi.CallCc(_ => (object?)42);
            }
            catch (SaveContinuation sce)
            {
                sce.Extend(new AddOneFrame());
                throw;
            }
            await Task.Yield();
            return new AddOneFrame().Invoke(x!);
        });
        Assert.Equal(43, result);
    }

    [Fact]
    public async Task RunAsync_ContinuationInvoked_AbortsAndResumes_AsyncFrames()
    {
        // Capture, then invoke continuation. The user fn calls k.Invoke(99), which throws
        // AbortAndResume — that exception escapes the user's try/catch (it's not a
        // SaveContinuation) and lands in RunAsync's outer handler, which dispatches to
        // ResumeAsync. The async frame replays without blocking.
        var result = await RuntimeApi.RunAsync(async () =>
        {
            object? x;
            try
            {
                x = RuntimeApi.CallCc(k => k.Invoke(99));
            }
            catch (SaveContinuation sce)
            {
                sce.Extend(new AsyncAddTwoFrame());
                throw;
            }
            await Task.Yield();
            return (int)x!;
        });
        // 99 → AsyncAddTwoFrame → 101.
        Assert.Equal(101, result);
    }

    [Fact]
    public async Task RunAsync_PropagatesUserExceptionsThroughTask()
    {
        // Non-SaveContinuation exceptions thrown from programMain should surface on the
        // returned task — async path must not swallow them.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RuntimeApi.RunAsync<int>(() => throw new InvalidOperationException("nope"))
        );
    }

    private sealed class ThrowingFrame : IFrame
    {
        private readonly SaveContinuation _ex;

        public ThrowingFrame(SaveContinuation ex) => _ex = ex;

        public object Invoke(object returnValue) => throw _ex;

        public Task<object> InvokeAsync(object returnValue) => throw _ex;
    }
}
