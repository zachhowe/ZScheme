namespace ZScript.Runtime.Tests;

using ZScript.Runtime;
using Xunit;

public class ZsResultTests
{
    [Fact]
    public void Ok_IsOk_ReturnsTrue()
    {
        var result = new ZsResult<int, string>.Ok(42);
        Assert.True(result.IsOk);
    }

    [Fact]
    public void Ok_IsErr_ReturnsFalse()
    {
        var result = new ZsResult<int, string>.Ok(42);
        Assert.False(result.IsErr);
    }

    [Fact]
    public void Err_IsErr_ReturnsTrue()
    {
        var result = new ZsResult<int, string>.Err("fail");
        Assert.True(result.IsErr);
    }

    [Fact]
    public void Err_IsOk_ReturnsFalse()
    {
        var result = new ZsResult<int, string>.Err("fail");
        Assert.False(result.IsOk);
    }

    [Fact]
    public void Ok_Unwrap_ReturnsValue()
    {
        var result = new ZsResult<int, string>.Ok(42);
        Assert.Equal(42, result.Unwrap());
    }

    [Fact]
    public void Err_Unwrap_ThrowsInvalidOperationException()
    {
        var result = new ZsResult<int, string>.Err("fail");
        var ex = Assert.Throws<InvalidOperationException>(() => result.Unwrap());
        Assert.Equal("Called Unwrap on Err", ex.Message);
    }

    [Fact]
    public void Ok_Map_TransformsValue()
    {
        var result = new ZsResult<int, string>.Ok(5);
        var mapped = result.Map(x => x * 2);
        Assert.IsType<ZsResult<int, string>.Ok>(mapped);
        Assert.Equal(10, mapped.Unwrap());
    }

    [Fact]
    public void Err_Map_PreservesError()
    {
        var result = new ZsResult<int, string>.Err("fail");
        var mapped = result.Map(x => x * 2);
        Assert.IsType<ZsResult<int, string>.Err>(mapped);
        var err = (ZsResult<int, string>.Err)mapped;
        Assert.Equal("fail", err.Error);
    }

    [Fact]
    public void Ok_Map_ChangesType()
    {
        var result = new ZsResult<int, string>.Ok(42);
        var mapped = result.Map(x => x.ToString());
        Assert.IsType<ZsResult<string, string>.Ok>(mapped);
        Assert.Equal("42", mapped.Unwrap());
    }

    [Fact]
    public void Ok_FlatMap_ReturnsInnerResult()
    {
        var result = new ZsResult<int, string>.Ok(5);
        var flat = result.FlatMap(x => new ZsResult<int, string>.Ok(x + 1));
        Assert.IsType<ZsResult<int, string>.Ok>(flat);
        Assert.Equal(6, flat.Unwrap());
    }

    [Fact]
    public void Ok_FlatMap_ReturningErr_ReturnsErr()
    {
        var result = new ZsResult<int, string>.Ok(5);
        var flat = result.FlatMap<int>(_ => new ZsResult<int, string>.Err("fail"));
        Assert.IsType<ZsResult<int, string>.Err>(flat);
    }

    [Fact]
    public void Err_FlatMap_PreservesError()
    {
        var result = new ZsResult<int, string>.Err("fail");
        var flat = result.FlatMap(x => new ZsResult<int, string>.Ok(x + 1));
        Assert.IsType<ZsResult<int, string>.Err>(flat);
        var err = (ZsResult<int, string>.Err)flat;
        Assert.Equal("fail", err.Error);
    }

    [Fact]
    public void Ok_ToString_FormatsCorrectly()
    {
        var result = new ZsResult<int, string>.Ok(42);
        Assert.Equal("Ok(42)", result.ToString());
    }

    [Fact]
    public void Err_ToString_FormatsCorrectly()
    {
        var result = new ZsResult<int, string>.Err("fail");
        Assert.Equal("Err(fail)", result.ToString());
    }

    [Fact]
    public void Ok_Equality_SameValue_AreEqual()
    {
        var a = new ZsResult<int, string>.Ok(42);
        var b = new ZsResult<int, string>.Ok(42);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Ok_Equality_DifferentValue_AreNotEqual()
    {
        var a = new ZsResult<int, string>.Ok(1);
        var b = new ZsResult<int, string>.Ok(2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Err_Equality_SameError_AreEqual()
    {
        var a = new ZsResult<int, string>.Err("x");
        var b = new ZsResult<int, string>.Err("x");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Err_Equality_DifferentError_AreNotEqual()
    {
        var a = new ZsResult<int, string>.Err("x");
        var b = new ZsResult<int, string>.Err("y");
        Assert.NotEqual(a, b);
    }
}
