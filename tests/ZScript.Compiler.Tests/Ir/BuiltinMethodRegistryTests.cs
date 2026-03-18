namespace ZScript.Compiler.Tests.Ir;

using ZScript.Compiler.Ir;
using Xunit;

public class BuiltinMethodRegistryTests
{
    [Fact]
    public void Registry_Contains28Entries()
    {
        Assert.Equal(28, BuiltinMethodRegistry.CollectionMethods.Count);
    }

    [Fact]
    public void ListHead_IsProperty()
    {
        var info = BuiltinMethodRegistry.CollectionMethods["list/head"];
        Assert.Equal("Head", info.CSharpName);
        Assert.True(info.IsProperty);
        Assert.False(info.IsIndexer);
    }

    [Fact]
    public void ListCons_IsMethod()
    {
        var info = BuiltinMethodRegistry.CollectionMethods["list/cons"];
        Assert.Equal("Cons", info.CSharpName);
        Assert.False(info.IsProperty);
        Assert.False(info.IsIndexer);
    }

    [Fact]
    public void ListNth_IsIndexer()
    {
        var info = BuiltinMethodRegistry.CollectionMethods["list/nth"];
        Assert.Equal("", info.CSharpName);
        Assert.True(info.IsProperty);
        Assert.True(info.IsIndexer);
    }

    [Fact]
    public void VectorNth_IsIndexer()
    {
        var info = BuiltinMethodRegistry.CollectionMethods["vector/nth"];
        Assert.True(info.IsIndexer);
    }

    [Fact]
    public void MapNth_IsIndexer()
    {
        var info = BuiltinMethodRegistry.CollectionMethods["map/nth"];
        Assert.True(info.IsIndexer);
    }

    [Fact]
    public void MapContainsKey_IsMethod()
    {
        var info = BuiltinMethodRegistry.CollectionMethods["map/contains-key?"];
        Assert.Equal("ContainsKey", info.CSharpName);
        Assert.False(info.IsProperty);
    }

    [Fact]
    public void AllKeys_FollowCollectionSlashMethodPattern()
    {
        foreach (var key in BuiltinMethodRegistry.CollectionMethods.Keys)
        {
            var parts = key.Split('/');
            Assert.Equal(2, parts.Length);
            Assert.Contains(parts[0], new[] { "list", "vector", "map" });
            Assert.False(string.IsNullOrEmpty(parts[1]));
        }
    }
}
