using Xunit;
using ZScheme.Runtime;

namespace ZScheme.Compiler.Tests.Runtime;

public class RuntimeShiftResetTests
{
    [Fact]
    public void Reset_NoShift_ReturnsBody()
    {
        var result = ZScheme.Runtime.Runtime.Reset(() => 5);
        Assert.Equal(5, result);
    }

    [Fact]
    public void Reset_ShiftWithComposedK_ReplaysFrames()
    {
        // Equivalent to (reset (+ 1 (shift k (k 10)))) — but built directly against the runtime
        // surface. Without the compiler-synthesized frame for "+ 1 _", the captured continuation
        // is empty and (k 10) returns 10, so the form yields 10.
        var result = ZScheme.Runtime.Runtime.Reset(() =>
            ZScheme.Runtime.Runtime.ShiftTyped<int, int>(k => k(10))
        );
        Assert.Equal(10, result);
    }

    [Fact]
    public void Reset_ShiftDiscardingK_YieldsShiftBody()
    {
        var result = ZScheme.Runtime.Runtime.Reset(() =>
            ZScheme.Runtime.Runtime.ShiftTyped<int, int>(_ => 99)
        );
        Assert.Equal(99, result);
    }

    [Fact]
    public void Reset_MultiShotShift_InvokesKMultipleTimes()
    {
        // (reset (shift k (+ (k 1) (k 2)))) — with no captured frames, k(v) = v, so + = 3.
        var result = ZScheme.Runtime.Runtime.Reset(() =>
            ZScheme.Runtime.Runtime.ShiftTyped<int, int>(k => k(1) + k(2))
        );
        Assert.Equal(3, result);
    }

    [Fact]
    public void NestedReset_InnerShiftTargetsInnermost()
    {
        // Inner shift captures up to inner reset; outer reset returns 1 + (inner result) = 1 + 99 = 100.
        var result = ZScheme.Runtime.Runtime.Reset(() =>
            1
            + ZScheme.Runtime.Runtime.Reset(() =>
                ZScheme.Runtime.Runtime.ShiftTyped<int, int>(_ => 99)
            )
        );
        Assert.Equal(100, result);
    }

    [Fact]
    public void Shift_OutsideReset_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ZScheme.Runtime.Runtime.ShiftTyped<int, int>(k => k(1))
        );
        Assert.Contains("(shift) used outside any (reset)", ex.Message);
    }

    [Fact]
    public void PromptStack_PoppedEvenWhenBodyThrows()
    {
        // After Reset throws (because its body threw something Reset doesn't catch), the prompt
        // tag must still be popped — otherwise the stack leaks across calls.
        Assert.Throws<InvalidOperationException>(() =>
            ZScheme.Runtime.Runtime.Reset<int>(() => throw new InvalidOperationException("boom"))
        );

        // A subsequent shift outside any reset must still report "outside any reset" — proving
        // the stack was cleaned.
        Assert.Throws<InvalidOperationException>(() =>
            ZScheme.Runtime.Runtime.ShiftTyped<int, int>(k => k(1))
        );
    }

    [Fact]
    public void DelimitedContinuation_IsComposable()
    {
        // Capture k; invoke it later from a fresh top-level. Composability means k(v) returns
        // a value (it does not abort), and the call site keeps running.
        Func<int, int>? captured = null;
        var first = ZScheme.Runtime.Runtime.Reset(() =>
            ZScheme.Runtime.Runtime.ShiftTyped<int, int>(k =>
            {
                captured = k;
                return 0;
            })
        );
        Assert.Equal(0, first);
        Assert.NotNull(captured);

        // Re-invoking the captured k returns the value (no Resume of "rest of program").
        Assert.Equal(7, captured!(7));
        Assert.Equal(8, captured!(8));
    }

    [Fact]
    public void PromptStack_IsThreadLocal()
    {
        // Two threads each Reset independently; their prompt stacks must not interfere.
        var t1Result = 0;
        var t2Result = 0;
        var t1 = new Thread(() =>
        {
            t1Result = ZScheme.Runtime.Runtime.Reset(() =>
                ZScheme.Runtime.Runtime.ShiftTyped<int, int>(k => k(11))
            );
        });
        var t2 = new Thread(() =>
        {
            t2Result = ZScheme.Runtime.Runtime.Reset(() =>
                ZScheme.Runtime.Runtime.ShiftTyped<int, int>(k => k(22))
            );
        });
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();
        Assert.Equal(11, t1Result);
        Assert.Equal(22, t2Result);
    }
}
