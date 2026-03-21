namespace ZScript.Runtime.Tests;

using ZScript.Runtime;
using Xunit;

public class ZsUnitTests
{
    [Fact]
    public void Value_IsSingleton()
    {
        var a = ZsUnit.Value;
        var b = ZsUnit.Value;
        Assert.Same(a, b);
    }

    [Fact]
    public void ToString_ReturnsParens()
    {
        Assert.Equal("()", ZsUnit.Value.ToString());
    }

    [Fact]
    public void Equals_AnotherZsUnit_ReturnsTrue()
    {
        Assert.True(ZsUnit.Value.Equals(ZsUnit.Value));
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        Assert.False(ZsUnit.Value.Equals(null));
    }

    [Fact]
    public void Equals_OtherType_ReturnsFalse()
    {
        Assert.False(ZsUnit.Value.Equals("hello"));
    }

    [Fact]
    public void GetHashCode_ReturnsZero()
    {
        Assert.Equal(0, ZsUnit.Value.GetHashCode());
    }
}
