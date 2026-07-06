using Xunit;
using ZScheme.Runtime;

namespace ZScheme.Compiler.Tests.Runtime;

public class ZSymbolTests
{
    [Fact]
    public void Intern_SameName_ReturnsSameInstance()
    {
        var a = ZSymbol.Intern("foo");
        var b = ZSymbol.Intern("foo");
        Assert.Same(a, b);
        Assert.True(a == b);
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Intern_DifferentNames_AreNotEqual()
    {
        var a = ZSymbol.Intern("foo");
        var b = ZSymbol.Intern("bar");
        Assert.NotSame(a, b);
        Assert.False(a == b);
        Assert.True(a != b);
    }

    [Fact]
    public void Name_And_ToString_ReturnTheSymbolName()
    {
        var s = ZSymbol.Intern("some-symbol");
        Assert.Equal("some-symbol", s.Name);
        Assert.Equal("some-symbol", s.ToString());
    }

    [Fact]
    public void GetHashCode_IsStableForSameName()
    {
        Assert.Equal(
            ZSymbol.Intern("hash-me").GetHashCode(),
            ZSymbol.Intern("hash-me").GetHashCode()
        );
    }
}
