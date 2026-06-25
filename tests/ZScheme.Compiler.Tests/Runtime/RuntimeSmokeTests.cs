using Xunit;
using ZScheme.Runtime;

namespace ZScheme.Compiler.Tests.Runtime;

/// <summary>
/// Hand-written tests that simulate what ContinuationTransform will eventually emit. These
/// verify the runtime contract end-to-end without depending on the compiler pass.
/// </summary>
public class RuntimeSmokeTests
{
    private sealed class AddOneFrame : IFrame
    {
        public object Invoke(object returnValue) => (int)returnValue! + 1;
    }

    [Fact]
    public void CallCc_NormalReturn_ReplaysFrames()
    {
        // Simulates: Run(() => { var x = call/cc(k => 42); return x + 1; })
        var result = ZScheme.Runtime.Runtime.Run(() =>
        {
            object? x;
            try
            {
                x = ZScheme.Runtime.Runtime.CallCc(_ => (object?)42);
            }
            catch (SaveContinuation sce)
            {
                sce.Extend(new AddOneFrame());
                throw;
            }
            return new AddOneFrame().Invoke(x!);
        });

        Assert.Equal(43, result);
    }

    [Fact]
    public void CallCc_ContinuationInvoked_AbortsAndResumes()
    {
        // Simulates: Run(() => { var x = call/cc(k => k(99)); return x + 1; })
        // The call (k 99) throws AbortAndResume(frames, 99).
        // Run catches, replays the captured frames with 99 as seed, threading through AddOneFrame.
        var result = ZScheme.Runtime.Runtime.Run(() =>
        {
            object? x;
            try
            {
                x = ZScheme.Runtime.Runtime.CallCc(k => k.Invoke(99));
            }
            catch (SaveContinuation sce)
            {
                sce.Extend(new AddOneFrame());
                throw;
            }
            return new AddOneFrame().Invoke(x!);
        });

        Assert.Equal(100, result);
    }

    private sealed class PassThroughFrame : IFrame
    {
        public object Invoke(object returnValue) => returnValue;
    }

    [Fact]
    public void CallCc_MultiShot_BothInvocationsRunIndependently()
    {
        // Simulates capturing a continuation, returning "first" once, then invoking it
        // a second time externally. The captured continuation's Invoke just passes the
        // value through (single-frame continuation that returns the value as-is).
        Continuation? saved = null;
        var firstResult = ZScheme.Runtime.Runtime.Run(() =>
        {
            object? x;
            try
            {
                x = ZScheme.Runtime.Runtime.CallCc(k =>
                {
                    saved = k;
                    return "first";
                });
            }
            catch (SaveContinuation sce)
            {
                sce.Extend(new PassThroughFrame());
                throw;
            }
            return (string)x!;
        });
        Assert.Equal("first", firstResult);
        Assert.NotNull(saved);

        // Invoke the saved continuation with a different value — it should replay through
        // the captured frame and return "second".
        var secondResult = ZScheme.Runtime.Runtime.Run<string>(() =>
        {
            return (string)saved!.Invoke("second")!;
        });
        Assert.Equal("second", secondResult);
    }
}
