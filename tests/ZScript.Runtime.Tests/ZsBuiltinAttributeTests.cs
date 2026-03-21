namespace ZScript.Runtime.Tests;

using System.Reflection;
using ZScript.Runtime;
using Xunit;

public class ZsBuiltinAttributeTests
{
    [Fact]
    public void Name_IsSetFromConstructor()
    {
        var attr = new ZsBuiltinAttribute("list/head");
        Assert.Equal("list/head", attr.Name);
    }

    [Fact]
    public void IsIndexer_DefaultsFalse()
    {
        var attr = new ZsBuiltinAttribute("list/head");
        Assert.False(attr.IsIndexer);
    }

    [Fact]
    public void IsIndexer_CanBeSetToTrue()
    {
        var attr = new ZsBuiltinAttribute("list/nth") { IsIndexer = true };
        Assert.True(attr.IsIndexer);
    }

    [Fact]
    public void AttributeUsage_TargetsPropertyAndMethod()
    {
        var usage = typeof(ZsBuiltinAttribute).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Property));
        Assert.True(usage.ValidOn.HasFlag(AttributeTargets.Method));
    }
}
