namespace ZScript.Runtime.Tests;

using ZScript.Runtime;
using Xunit;

public class ZsVectorTests
{
    [Fact]
    public void Empty_IsEmpty_ReturnsTrue()
    {
        Assert.True(ZsVector<int>.Empty.IsEmpty);
    }

    [Fact]
    public void Empty_Count_ReturnsZero()
    {
        Assert.Equal(0, ZsVector<int>.Empty.Count);
    }

    [Fact]
    public void FromItems_CreatesVectorWithCorrectCount()
    {
        var vec = ZsVector<int>.FromItems(1, 2, 3);
        Assert.Equal(3, vec.Count);
    }

    [Fact]
    public void FromItems_PreservesOrder()
    {
        var vec = ZsVector<int>.FromItems(10, 20, 30);
        Assert.Equal(10, vec[0]);
        Assert.Equal(20, vec[1]);
        Assert.Equal(30, vec[2]);
    }

    [Fact]
    public void Indexer_ValidIndex_ReturnsElement()
    {
        var vec = ZsVector<int>.FromItems(10, 20, 30);
        Assert.Equal(20, vec[1]);
    }

    [Fact]
    public void Indexer_OutOfRange_ThrowsException()
    {
        var vec = ZsVector<int>.FromItems(1);
        Assert.ThrowsAny<Exception>(() => vec[5]);
    }

    [Fact]
    public void Append_AddsToEnd()
    {
        var vec = ZsVector<int>.FromItems(1, 2);
        var result = vec.Append(3);
        Assert.Equal(3, result.Count);
        Assert.Equal(3, result[2]);
    }

    [Fact]
    public void Set_ReplacesAtIndex()
    {
        var vec = ZsVector<int>.FromItems(1, 2, 3);
        var result = vec.Set(1, 99);
        Assert.Equal(99, result[1]);
        Assert.Equal(1, result[0]);
        Assert.Equal(3, result[2]);
    }

    [Fact]
    public void Set_DoesNotMutateOriginal()
    {
        var original = ZsVector<int>.FromItems(1, 2, 3);
        _ = original.Set(1, 99);
        Assert.Equal(2, original[1]);
    }

    [Fact]
    public void Map_TransformsElements()
    {
        var vec = ZsVector<int>.FromItems(1, 2, 3);
        var result = vec.Map(x => x * 10);
        Assert.Equal(3, result.Count);
        Assert.Equal(10, result[0]);
        Assert.Equal(20, result[1]);
        Assert.Equal(30, result[2]);
    }

    [Fact]
    public void Map_EmptyVector_ReturnsEmpty()
    {
        var result = ZsVector<int>.Empty.Map(x => x * 2);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Filter_SelectsMatchingElements()
    {
        var vec = ZsVector<int>.FromItems(1, 2, 3, 4);
        var result = vec.Filter(x => x % 2 == 0);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[0]);
        Assert.Equal(4, result[1]);
    }

    [Fact]
    public void Filter_NoMatches_ReturnsEmpty()
    {
        var vec = ZsVector<int>.FromItems(1, 3, 5);
        var result = vec.Filter(x => x % 2 == 0);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Fold_AccumulatesResult()
    {
        var vec = ZsVector<int>.FromItems(1, 2, 3);
        var result = vec.Fold(0, (acc, x) => acc + x);
        Assert.Equal(6, result);
    }

    [Fact]
    public void Fold_EmptyVector_ReturnsSeed()
    {
        var result = ZsVector<int>.Empty.Fold(42, (acc, x) => acc + x);
        Assert.Equal(42, result);
    }

    [Fact]
    public void ToString_FormatsWithHashBrackets()
    {
        var vec = ZsVector<int>.FromItems(1, 2, 3);
        Assert.Equal("#[1, 2, 3]", vec.ToString());
    }

    [Fact]
    public void ToString_Empty_ReturnsEmptyHashBrackets()
    {
        Assert.Equal("#[]", ZsVector<int>.Empty.ToString());
    }

    [Fact]
    public void GetEnumerator_EnumeratesAllElements()
    {
        var vec = ZsVector<int>.FromItems(1, 2, 3);
        var collected = new List<int>();
        foreach (var item in vec)
            collected.Add(item);
        Assert.Equal([1, 2, 3], collected);
    }

    [Fact]
    public void IsEmpty_NonEmptyVector_ReturnsFalse()
    {
        var vec = ZsVector<int>.FromItems(1);
        Assert.False(vec.IsEmpty);
    }

    [Fact]
    public void Of_HelperCreatesVectorCorrectly()
    {
        var vec = ZsVector.Of(1, 2, 3);
        Assert.Equal(3, vec.Count);
        Assert.Equal(1, vec[0]);
        Assert.Equal(2, vec[1]);
        Assert.Equal(3, vec[2]);
    }

    [Fact]
    public void Immutability_OriginalUnchangedAfterAppend()
    {
        var original = ZsVector<int>.FromItems(1, 2);
        _ = original.Append(3);
        Assert.Equal(2, original.Count);
    }
}
