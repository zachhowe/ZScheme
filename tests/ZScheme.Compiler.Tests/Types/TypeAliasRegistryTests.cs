using System.Collections.Immutable;
using System.Collections.Generic;
using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Types;

namespace ZScheme.Compiler.Tests.Types;

public class TypeAliasRegistryTests
{
    [Fact]
    public void TryGetZsNameFromClrType_MutableVector_ReturnsMutableVector()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Mutable-Vector", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(int[]), out var zsName));
        Assert.Equal("Mutable-Vector", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_MutableHash_ReturnsMutableHash()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Mutable-Hash", ["^k", "^v"], "System.Collections.Generic.Dictionary", null, TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(Dictionary<string, int>), out var zsName));
        Assert.Equal("Mutable-Hash", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_MutableList_ReturnsMutableList()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Mutable-List", ["^a"], "System.Collections.Generic.List", null, TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(List<int>), out var zsName));
        Assert.Equal("Mutable-List", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_ConcurrentQueue_ReturnsConcurrentQueue()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Concurrent-Queue", ["^a"], "System.Collections.Concurrent.ConcurrentQueue", "System.Collections.Concurrent", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(System.Collections.Concurrent.ConcurrentQueue<int>), out var zsName));
        Assert.Equal("Concurrent-Queue", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_ConcurrentDictionary_ReturnsConcurrentDictionary()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Concurrent-Dictionary", ["^k", "^v"], "System.Collections.Concurrent.ConcurrentDictionary", "System.Collections.Concurrent", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(System.Collections.Concurrent.ConcurrentDictionary<string, int>), out var zsName));
        Assert.Equal("Concurrent-Dictionary", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_Vector_ReturnsVector()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Vector", ["^a"], "System.Collections.Immutable.ImmutableArray", "System.Collections.Immutable", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(ImmutableArray<int>), out var zsName));
        Assert.Equal("Vector", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_Hash_ReturnsHash()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Hash", ["^k", "^v"], "System.Collections.Immutable.ImmutableDictionary", "System.Collections.Immutable", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(ImmutableDictionary<string, int>), out var zsName));
        Assert.Equal("Hash", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_Pair_ReturnsPair()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Pair", ["^k", "^v"], "System.Collections.Generic.KeyValuePair", "System.Collections.Generic", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(KeyValuePair<string, int>), out var zsName));
        Assert.Equal("Pair", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_List_ReturnsList()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("List", ["^a"], "System.Collections.Immutable.ImmutableList", "System.Collections.Immutable", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(ImmutableList<int>), out var zsName));
        Assert.Equal("List", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_ConcurrentBag_ReturnsConcurrentBag()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Concurrent-Bag", ["^a"], "System.Collections.Concurrent.ConcurrentBag", "System.Collections.Concurrent", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(System.Collections.Concurrent.ConcurrentBag<int>), out var zsName));
        Assert.Equal("Concurrent-Bag", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_ConcurrentStack_ReturnsConcurrentStack()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Concurrent-Stack", ["^a"], "System.Collections.Concurrent.ConcurrentStack", "System.Collections.Concurrent", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(System.Collections.Concurrent.ConcurrentStack<int>), out var zsName));
        Assert.Equal("Concurrent-Stack", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_UnregisteredType_ReturnsFalse()
    {
        var registry = new TypeAliasRegistry();
        Assert.False(registry.TryGetZsNameFromClrType(typeof(Dictionary<string, int>), out _));
    }

    [Fact]
    public void TryGetZsNameFromClrType_MultipleAliases_DistinctTypes()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Mutable-Hash", ["^k", "^v"], "System.Collections.Generic.Dictionary", null, TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        registry.TryAdd(new TypeAliasInfo("Hash", ["^k", "^v"], "System.Collections.Immutable.ImmutableDictionary", "System.Collections.Immutable", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(Dictionary<string, int>), out var mutableHashName));
        Assert.Equal("Mutable-Hash", mutableHashName);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(ImmutableDictionary<string, int>), out var hashName));
        Assert.Equal("Hash", hashName);
    }

    [Fact]
    public void TryGetFirstArrayAliasName_WithMutableVector_ReturnsMutableVector()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Mutable-Vector", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        Assert.True(registry.TryGetFirstArrayAliasName(out var name));
        Assert.Equal("Mutable-Vector", name);
    }

    [Fact]
    public void TryGetFirstArrayAliasName_WithCustomAlias_ReturnsCustomAlias()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Custom-Array", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        Assert.True(registry.TryGetFirstArrayAliasName(out var name));
        Assert.Equal("Custom-Array", name);
    }

    [Fact]
    public void TryGetFirstArrayAliasName_WithNoArrayAlias_ReturnsFalse()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("List", ["^a"], "System.Collections.Immutable.ImmutableList", "System.Collections.Immutable", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.False(registry.TryGetFirstArrayAliasName(out _));
    }

    [Fact]
    public void TryGetFirstArrayAliasName_WithMultipleAliases_ReturnsFirstRegistered()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Mutable-Vector", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        registry.TryAdd(new TypeAliasInfo("Custom-Array", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        Assert.True(registry.TryGetFirstArrayAliasName(out var name));
        Assert.Equal("Mutable-Vector", name);
    }

    [Fact]
    public void IsArrayName_WithMutableVector_ReturnsTrue()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Mutable-Vector", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        Assert.True(registry.IsArrayName("Mutable-Vector"));
    }

    [Fact]
    public void IsArrayName_WithCustomArrayAlias_ReturnsTrue()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Custom-Array", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        Assert.True(registry.IsArrayName("Custom-Array"));
    }

    [Fact]
    public void IsArrayName_WithNonArrayAlias_ReturnsFalse()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("List", ["^a"], "System.Collections.Immutable.ImmutableList", "System.Collections.Immutable", TypeAliasKind.GenericClrType, SourceSpan.None), out _);
        Assert.False(registry.IsArrayName("List"));
    }

    [Fact]
    public void IsArrayName_WithUnregisteredName_ReturnsFalse()
    {
        var registry = new TypeAliasRegistry();
        Assert.False(registry.IsArrayName("Not-Registered"));
    }

    [Fact]
    public void TryGetZsNameFromClrType_WithCustomArrayAlias_ReturnsCustomAlias()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Custom-Array", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(int[]), out var zsName));
        Assert.Equal("Custom-Array", zsName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_CustomArrayAlias_MatchesSpecificElementType()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Byte-Array", ["^a"], "System.Byte", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        registry.TryAdd(new TypeAliasInfo("Int-Array", ["^a"], "System.Int32", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(byte[]), out var byteName));
        Assert.Equal("Byte-Array", byteName);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(int[]), out var intName));
        Assert.Equal("Int-Array", intName);
    }

    [Fact]
    public void TryGetZsNameFromClrType_StdlibAndCustomArray_AnyArrayMatchesStdlib()
    {
        var registry = new TypeAliasRegistry();
        registry.TryAdd(new TypeAliasInfo("Mutable-Vector", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        registry.TryAdd(new TypeAliasInfo("Custom-Array", ["^a"], "", null, TypeAliasKind.SzArray, SourceSpan.None), out _);
        Assert.True(registry.TryGetZsNameFromClrType(typeof(int[]), out var zsName));
        Assert.Equal("Mutable-Vector", zsName);
    }
}
