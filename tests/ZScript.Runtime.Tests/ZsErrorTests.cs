namespace ZScript.Runtime.Tests;

using ZScript.Runtime;
using Xunit;

public class ZsErrorTests
{
    [Fact]
    public void Constructor_MessageOnly_SetsCauseToNone()
    {
        var error = new ZsError("oops");
        Assert.Equal("oops", error.Message);
        Assert.True(error.Cause.IsNone);
    }

    [Fact]
    public void Constructor_MessageAndCause_BothSet()
    {
        var inner = new ZsError("inner");
        var outer = new ZsError("outer", new ZsOption<ZsError>.Some(inner));
        Assert.Equal("outer", outer.Message);
        Assert.True(outer.Cause.IsSome);
        Assert.Equal(inner, outer.Cause.Unwrap());
    }

    [Fact]
    public void ToString_NoCause_FormatsCorrectly()
    {
        var error = new ZsError("oops");
        Assert.Equal("ZsError(oops)", error.ToString());
    }

    [Fact]
    public void ToString_WithCause_IncludesCausedBy()
    {
        var inner = new ZsError("inner");
        var outer = new ZsError("outer", new ZsOption<ZsError>.Some(inner));
        Assert.Equal("ZsError(outer, caused by: ZsError(inner))", outer.ToString());
    }

    [Fact]
    public void ToString_NestedCauseChain_FormatsRecursively()
    {
        var root = new ZsError("root");
        var mid = new ZsError("mid", new ZsOption<ZsError>.Some(root));
        var top = new ZsError("top", new ZsOption<ZsError>.Some(mid));
        Assert.Equal("ZsError(top, caused by: ZsError(mid, caused by: ZsError(root)))", top.ToString());
    }

    [Fact]
    public void Equality_SameMessage_NoCause_AreEqual()
    {
        var a = new ZsError("oops");
        var b = new ZsError("oops");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentMessage_AreNotEqual()
    {
        var a = new ZsError("oops");
        var b = new ZsError("fail");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_SameMessageDifferentCause_AreNotEqual()
    {
        var a = new ZsError("oops");
        var b = new ZsError("oops", new ZsOption<ZsError>.Some(new ZsError("cause")));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Cause_IsSome_WhenCauseProvided()
    {
        var error = new ZsError("outer", new ZsOption<ZsError>.Some(new ZsError("inner")));
        Assert.True(error.Cause.IsSome);
    }

    [Fact]
    public void Cause_IsNone_WhenNoCause()
    {
        var error = new ZsError("oops");
        Assert.True(error.Cause.IsNone);
    }
}
