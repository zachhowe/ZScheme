using Xunit;
using ZScheme.Runtime;

namespace ZScheme.Compiler.Tests.Runtime;

public class RuntimeControlPromptTests
{
    [Fact]
    public void Control_DiscardingK_YieldsControlBody()
    {
        var result = ZScheme.Runtime.Runtime.Reset(() =>
            ZScheme.Runtime.Runtime.ControlTyped<int, int>(_ => 99)
        );
        Assert.Equal(99, result);
    }

    [Fact]
    public void Control_OutsidePrompt_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ZScheme.Runtime.Runtime.ControlTyped<int, int>(k => k(1))
        );
        Assert.Contains("(control)", ex.Message);
    }

    [Fact]
    public void CallComp_DesugarsToControl()
    {
        // (call/comp f) ≡ (control k (f k)). With no captured frames the continuation
        // returns its argument verbatim, so f(k) where f = identity returns k itself which
        // we cannot return as int — exercise it with a body that invokes k instead.
        var result = ZScheme.Runtime.Runtime.Reset(() =>
            ZScheme.Runtime.Runtime.CallCompTyped<int, int>(k => k(42))
        );
        Assert.Equal(42, result);
    }

    [Fact]
    public void MakePromptTag_ReturnsDistinctTags()
    {
        var t1 = ZScheme.Runtime.Runtime.MakePromptTag();
        var t2 = ZScheme.Runtime.Runtime.MakePromptTag();
        Assert.NotSame(t1, t2);
    }

    [Fact]
    public void ResetAt_RoundTripsTaggedShift()
    {
        var tag = ZScheme.Runtime.Runtime.MakePromptTag();
        var result = ZScheme.Runtime.Runtime.ResetAt(
            tag,
            () => ZScheme.Runtime.Runtime.ShiftTypedAt<int, int>(tag, k => k(10))
        );
        Assert.Equal(10, result);
    }

    [Fact]
    public void TaggedShift_PassesThroughInnerPromptWithDifferentTag()
    {
        var outer = ZScheme.Runtime.Runtime.MakePromptTag();
        // Inner default-tagged Reset doesn't catch the outer-tagged shift; the throw escapes
        // to the outer ResetAt and we observe its capture.
        var result = ZScheme.Runtime.Runtime.ResetAt(
            outer,
            () =>
                ZScheme.Runtime.Runtime.Reset(() =>
                    ZScheme.Runtime.Runtime.ShiftTypedAt<int, int>(outer, _ => 77)
                )
        );
        Assert.Equal(77, result);
    }

    [Fact]
    public void ControlAt_PassesThroughInnerPromptWithDifferentTag()
    {
        var outer = ZScheme.Runtime.Runtime.MakePromptTag();
        var result = ZScheme.Runtime.Runtime.ResetAt(
            outer,
            () =>
                ZScheme.Runtime.Runtime.Reset(() =>
                    ZScheme.Runtime.Runtime.ControlTypedAt<int, int>(outer, _ => 88)
                )
        );
        Assert.Equal(88, result);
    }

    [Fact]
    public void TaggedShift_MissingTag_Throws()
    {
        var orphan = ZScheme.Runtime.Runtime.MakePromptTag();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ZScheme.Runtime.Runtime.ResetAt(
                ZScheme.Runtime.Runtime.MakePromptTag(),
                () => ZScheme.Runtime.Runtime.ShiftTypedAt<int, int>(orphan, k => k(1))
            )
        );
        Assert.Contains("not on the dynamic prompt stack", ex.Message);
    }

    [Fact]
    public void TaggedControl_MissingTag_Throws()
    {
        var orphan = ZScheme.Runtime.Runtime.MakePromptTag();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ZScheme.Runtime.Runtime.ResetAt(
                ZScheme.Runtime.Runtime.MakePromptTag(),
                () => ZScheme.Runtime.Runtime.ControlTypedAt<int, int>(orphan, k => k(1))
            )
        );
        Assert.Contains("not on the dynamic prompt stack", ex.Message);
    }

    [Fact]
    public void TaggedCallComp_MissingTag_Throws()
    {
        var orphan = ZScheme.Runtime.Runtime.MakePromptTag();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ZScheme.Runtime.Runtime.ResetAt(
                ZScheme.Runtime.Runtime.MakePromptTag(),
                () => ZScheme.Runtime.Runtime.CallCompTypedAt<int, int>(orphan, k => k(1))
            )
        );
        Assert.Contains("not on the dynamic prompt stack", ex.Message);
    }

    [Fact]
    public void ComposableContinuation_DoesNotInstallFreshPrompt()
    {
        // ComposableContinuation.Invoke replays frames without wrapping in Reset. With no
        // captured frames the difference vs. DelimitedContinuation is invisible at the value
        // level, but we can check stack discipline: invoking after the Reset is gone should
        // just return the value (no prompt is installed by the resume path).
        ComposableContinuation<int, int>? captured = null;
        var first = ZScheme.Runtime.Runtime.Reset(() =>
            ZScheme.Runtime.Runtime.ControlTyped<int, int>(k =>
            {
                // The body received a Func<int,int>; reach the underlying class for a direct
                // identity check.
                captured = ExtractComposable(k);
                return 0;
            })
        );
        Assert.Equal(0, first);
        Assert.NotNull(captured);
        Assert.Equal(7, captured!.Invoke(7));
    }

    // Extracts the ComposableContinuation backing a Func<TIn,TAns> wrapper produced by
    // ControlTyped. The wrapper is `v => k.Invoke(v)` where `k` is a ComposableContinuation;
    // we reflect into the closure for the test.
    private static ComposableContinuation<int, int>? ExtractComposable(Func<int, int> wrapper)
    {
        // The closure target holds the original ComposableContinuation as a captured field.
        var target = wrapper.Target;
        if (target is null)
            return null;
        foreach (
            var f in target
                .GetType()
                .GetFields(
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public
                )
        )
        {
            if (f.GetValue(target) is ComposableContinuation<int, int> cc)
                return cc;
        }
        return null;
    }
}
