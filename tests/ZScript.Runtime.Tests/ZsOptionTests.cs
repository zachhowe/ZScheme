namespace ZScript.Runtime.Tests;

using ZScript.Runtime;
using Xunit;

public class ZsOptionTests
{
    [Fact]
    public void Some_IsSome_ReturnsTrue()
    {
        var opt = new ZsOption<int>.Some(42);
        Assert.True(opt.IsSome);
    }

    [Fact]
    public void Some_IsNone_ReturnsFalse()
    {
        var opt = new ZsOption<int>.Some(42);
        Assert.False(opt.IsNone);
    }

    [Fact]
    public void None_IsNone_ReturnsTrue()
    {
        var opt = new ZsOption<int>.None();
        Assert.True(opt.IsNone);
    }

    [Fact]
    public void None_IsSome_ReturnsFalse()
    {
        var opt = new ZsOption<int>.None();
        Assert.False(opt.IsSome);
    }

    [Fact]
    public void Some_Unwrap_ReturnsValue()
    {
        var opt = new ZsOption<int>.Some(42);
        Assert.Equal(42, opt.Unwrap());
    }

    [Fact]
    public void None_Unwrap_ThrowsInvalidOperationException()
    {
        var opt = new ZsOption<int>.None();
        var ex = Assert.Throws<InvalidOperationException>(() => opt.Unwrap());
        Assert.Equal("Called Unwrap on None", ex.Message);
    }

    [Fact]
    public void Some_UnwrapOr_ReturnsValue()
    {
        var opt = new ZsOption<int>.Some(42);
        Assert.Equal(42, opt.UnwrapOr(99));
    }

    [Fact]
    public void None_UnwrapOr_ReturnsDefault()
    {
        var opt = new ZsOption<int>.None();
        Assert.Equal(99, opt.UnwrapOr(99));
    }

    [Fact]
    public void Some_Map_TransformsValue()
    {
        var opt = new ZsOption<int>.Some(5);
        var result = opt.Map(x => x * 2);
        Assert.IsType<ZsOption<int>.Some>(result);
        Assert.Equal(10, result.Unwrap());
    }

    [Fact]
    public void None_Map_ReturnsNone()
    {
        var opt = new ZsOption<int>.None();
        var result = opt.Map(x => x * 2);
        Assert.IsType<ZsOption<int>.None>(result);
    }

    [Fact]
    public void Some_Map_ChangesType()
    {
        var opt = new ZsOption<int>.Some(42);
        var result = opt.Map(x => x.ToString());
        Assert.IsType<ZsOption<string>.Some>(result);
        Assert.Equal("42", result.Unwrap());
    }

    [Fact]
    public void Some_FlatMap_ReturnsInnerResult()
    {
        var opt = new ZsOption<int>.Some(5);
        var result = opt.FlatMap(x => new ZsOption<int>.Some(x + 1));
        Assert.IsType<ZsOption<int>.Some>(result);
        Assert.Equal(6, result.Unwrap());
    }

    [Fact]
    public void Some_FlatMap_ReturningNone_ReturnsNone()
    {
        var opt = new ZsOption<int>.Some(5);
        var result = opt.FlatMap<int>(_ => new ZsOption<int>.None());
        Assert.IsType<ZsOption<int>.None>(result);
    }

    [Fact]
    public void None_FlatMap_ReturnsNone()
    {
        var opt = new ZsOption<int>.None();
        var result = opt.FlatMap(x => new ZsOption<int>.Some(x + 1));
        Assert.IsType<ZsOption<int>.None>(result);
    }

    [Fact]
    public void Some_ToString_FormatsCorrectly()
    {
        var opt = new ZsOption<int>.Some(42);
        Assert.Equal("Some(42)", opt.ToString());
    }

    [Fact]
    public void None_ToString_FormatsCorrectly()
    {
        var opt = new ZsOption<int>.None();
        Assert.Equal("None", opt.ToString());
    }

    [Fact]
    public void Some_Value_CanBeAccessed()
    {
        var opt = new ZsOption<string>.Some("hello");
        Assert.Equal("hello", opt.Value);
    }

    [Fact]
    public void Some_Equality_SameValue_AreEqual()
    {
        var a = new ZsOption<int>.Some(42);
        var b = new ZsOption<int>.Some(42);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Some_Equality_DifferentValue_AreNotEqual()
    {
        var a = new ZsOption<int>.Some(1);
        var b = new ZsOption<int>.Some(2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void None_Equality_TwoNones_AreEqual()
    {
        var a = new ZsOption<int>.None();
        var b = new ZsOption<int>.None();
        Assert.Equal(a, b);
    }
}
