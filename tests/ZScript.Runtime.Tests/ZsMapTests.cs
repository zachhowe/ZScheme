namespace ZScript.Runtime.Tests;

using ZScript.Runtime;
using Xunit;

public class ZsMapTests
{
    [Fact]
    public void Empty_IsEmpty_ReturnsTrue()
    {
        Assert.True(ZsMap<string, int>.Empty.IsEmpty);
    }

    [Fact]
    public void Empty_Count_ReturnsZero()
    {
        Assert.Equal(0, ZsMap<string, int>.Empty.Count);
    }

    [Fact]
    public void FromPairs_CreatesMapWithCorrectCount()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1), ("b", 2));
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void FromPairs_DuplicateKeys_LastWins()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1), ("a", 2));
        Assert.Equal(1, map.Count);
        Assert.Equal(2, map["a"]);
    }

    [Fact]
    public void Indexer_ExistingKey_ReturnsValue()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1), ("b", 2));
        Assert.Equal(1, map["a"]);
    }

    [Fact]
    public void Indexer_MissingKey_ThrowsKeyNotFoundException()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1));
        Assert.Throws<KeyNotFoundException>(() => map["z"]);
    }

    [Fact]
    public void Get_ExistingKey_ReturnsSome()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1));
        var result = map.Get("a");
        Assert.IsType<ZsOption<int>.Some>(result);
        Assert.Equal(1, result.Unwrap());
    }

    [Fact]
    public void Get_MissingKey_ReturnsNone()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1));
        var result = map.Get("z");
        Assert.IsType<ZsOption<int>.None>(result);
    }

    [Fact]
    public void Put_NewKey_IncreasesCount()
    {
        var map = ZsMap<string, int>.Empty.Put("a", 1);
        Assert.Equal(1, map.Count);
        Assert.Equal(1, map["a"]);
    }

    [Fact]
    public void Put_ExistingKey_ReplacesValue()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1), ("b", 2));
        var result = map.Put("a", 99);
        Assert.Equal(99, result["a"]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Put_DoesNotMutateOriginal()
    {
        var original = ZsMap<string, int>.FromPairs(("a", 1));
        _ = original.Put("a", 99);
        Assert.Equal(1, original["a"]);
    }

    [Fact]
    public void Remove_ExistingKey_DecreasesCount()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1), ("b", 2));
        var result = map.Remove("a");
        Assert.Equal(1, result.Count);
        Assert.False(result.ContainsKey("a"));
    }

    [Fact]
    public void Remove_MissingKey_ReturnsSameCount()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1));
        var result = map.Remove("z");
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void Remove_DoesNotMutateOriginal()
    {
        var original = ZsMap<string, int>.FromPairs(("a", 1), ("b", 2));
        _ = original.Remove("a");
        Assert.Equal(2, original.Count);
        Assert.True(original.ContainsKey("a"));
    }

    [Fact]
    public void ContainsKey_ExistingKey_ReturnsTrue()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1));
        Assert.True(map.ContainsKey("a"));
    }

    [Fact]
    public void ContainsKey_MissingKey_ReturnsFalse()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1));
        Assert.False(map.ContainsKey("z"));
    }

    [Fact]
    public void Keys_ReturnsAllKeys()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1), ("b", 2));
        var keys = map.Keys;
        Assert.Equal(2, keys.Count);
        var keySet = new HashSet<string>();
        foreach (var k in keys) keySet.Add(k);
        Assert.Contains("a", keySet);
        Assert.Contains("b", keySet);
    }

    [Fact]
    public void Values_ReturnsAllValues()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1), ("b", 2));
        var values = map.Values;
        Assert.Equal(2, values.Count);
        var valueSet = new HashSet<int>();
        foreach (var v in values) valueSet.Add(v);
        Assert.Contains(1, valueSet);
        Assert.Contains(2, valueSet);
    }

    [Fact]
    public void IsEmpty_NonEmptyMap_ReturnsFalse()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1));
        Assert.False(map.IsEmpty);
    }

    [Fact]
    public void ToString_SingleEntry_FormatsWithBraces()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1));
        Assert.Equal("{a: 1}", map.ToString());
    }

    [Fact]
    public void ToString_Empty_ReturnsEmptyBraces()
    {
        Assert.Equal("{}", ZsMap<string, int>.Empty.ToString());
    }

    [Fact]
    public void GetEnumerator_EnumeratesAllPairs()
    {
        var map = ZsMap<string, int>.FromPairs(("a", 1), ("b", 2));
        var collected = new Dictionary<string, int>();
        foreach (var kvp in map)
            collected[kvp.Key] = kvp.Value;
        Assert.Equal(2, collected.Count);
        Assert.Equal(1, collected["a"]);
        Assert.Equal(2, collected["b"]);
    }

    [Fact]
    public void Of_HelperCreatesMapCorrectly()
    {
        var map = ZsMap.Of(("a", 1), ("b", 2));
        Assert.Equal(2, map.Count);
        Assert.Equal(1, map["a"]);
        Assert.Equal(2, map["b"]);
    }

    [Fact]
    public void Get_ReturnType_IsSomeWithCorrectValue()
    {
        var map = ZsMap<string, int>.FromPairs(("x", 42));
        var result = map.Get("x");
        var some = Assert.IsType<ZsOption<int>.Some>(result);
        Assert.Equal(42, some.Value);
    }
}
