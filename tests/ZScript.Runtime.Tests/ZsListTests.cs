namespace ZScript.Runtime.Tests;

using ZScript.Runtime;
using Xunit;

public class ZsListTests
{
    [Fact]
    public void Empty_IsEmpty_ReturnsTrue()
    {
        Assert.True(ZsList<int>.Empty.IsEmpty);
    }

    [Fact]
    public void Empty_Count_ReturnsZero()
    {
        Assert.Equal(0, ZsList<int>.Empty.Count);
    }

    [Fact]
    public void FromItems_CreatesListWithCorrectCount()
    {
        var list = ZsList<int>.FromItems(1, 2, 3);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void FromItems_PreservesOrder()
    {
        var list = ZsList<int>.FromItems(10, 20, 30);
        Assert.Equal(10, list[0]);
        Assert.Equal(20, list[1]);
        Assert.Equal(30, list[2]);
    }

    [Fact]
    public void Head_NonEmptyList_ReturnsFirstElement()
    {
        var list = ZsList<int>.FromItems(10, 20, 30);
        Assert.Equal(10, list.Head);
    }

    [Fact]
    public void Head_EmptyList_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ZsList<int>.Empty.Head);
        Assert.Equal("Head of empty list", ex.Message);
    }

    [Fact]
    public void Tail_NonEmptyList_ReturnsRemainder()
    {
        var list = ZsList<int>.FromItems(10, 20, 30);
        var tail = list.Tail;
        Assert.Equal(2, tail.Count);
        Assert.Equal(20, tail.Head);
    }

    [Fact]
    public void Tail_SingleElement_ReturnsEmpty()
    {
        var list = ZsList<int>.FromItems(1);
        Assert.True(list.Tail.IsEmpty);
    }

    [Fact]
    public void Tail_EmptyList_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ZsList<int>.Empty.Tail);
        Assert.Equal("Tail of empty list", ex.Message);
    }

    [Fact]
    public void Cons_PrependsElement()
    {
        var list = ZsList<int>.FromItems(2, 3);
        var result = list.Cons(1);
        Assert.Equal(3, result.Count);
        Assert.Equal(1, result.Head);
    }

    [Fact]
    public void Cons_OnEmpty_CreatesSingletonList()
    {
        var result = ZsList<int>.Empty.Cons(42);
        Assert.Equal(1, result.Count);
        Assert.Equal(42, result.Head);
    }

    [Fact]
    public void Append_AddsToEnd()
    {
        var list = ZsList<int>.FromItems(1, 2);
        var result = list.Append(3);
        Assert.Equal(3, result.Count);
        Assert.Equal(3, result[2]);
    }

    [Fact]
    public void Concat_CombinesTwoLists()
    {
        var a = ZsList<int>.FromItems(1, 2);
        var b = ZsList<int>.FromItems(3, 4);
        var result = a.Concat(b);
        Assert.Equal(4, result.Count);
        Assert.Equal(1, result[0]);
        Assert.Equal(2, result[1]);
        Assert.Equal(3, result[2]);
        Assert.Equal(4, result[3]);
    }

    [Fact]
    public void Concat_WithEmpty_ReturnsSameElements()
    {
        var list = ZsList<int>.FromItems(1);
        var result = list.Concat(ZsList<int>.Empty);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public void Map_TransformsElements()
    {
        var list = ZsList<int>.FromItems(1, 2, 3);
        var result = list.Map(x => x * 10);
        Assert.Equal(3, result.Count);
        Assert.Equal(10, result[0]);
        Assert.Equal(20, result[1]);
        Assert.Equal(30, result[2]);
    }

    [Fact]
    public void Map_EmptyList_ReturnsEmpty()
    {
        var result = ZsList<int>.Empty.Map(x => x * 2);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Filter_SelectsMatchingElements()
    {
        var list = ZsList<int>.FromItems(1, 2, 3, 4);
        var result = list.Filter(x => x % 2 == 0);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[0]);
        Assert.Equal(4, result[1]);
    }

    [Fact]
    public void Filter_NoMatches_ReturnsEmpty()
    {
        var list = ZsList<int>.FromItems(1, 3, 5);
        var result = list.Filter(x => x % 2 == 0);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Fold_AccumulatesResult()
    {
        var list = ZsList<int>.FromItems(1, 2, 3);
        var result = list.Fold(0, (acc, x) => acc + x);
        Assert.Equal(6, result);
    }

    [Fact]
    public void Fold_EmptyList_ReturnsSeed()
    {
        var result = ZsList<int>.Empty.Fold(42, (acc, x) => acc + x);
        Assert.Equal(42, result);
    }

    [Fact]
    public void Indexer_ValidIndex_ReturnsElement()
    {
        var list = ZsList<int>.FromItems(10, 20, 30);
        Assert.Equal(20, list[1]);
    }

    [Fact]
    public void Indexer_OutOfRange_ThrowsException()
    {
        var list = ZsList<int>.FromItems(1);
        Assert.ThrowsAny<Exception>(() => list[5]);
    }

    [Fact]
    public void ToString_FormatsWithBrackets()
    {
        var list = ZsList<int>.FromItems(1, 2, 3);
        Assert.Equal("[1, 2, 3]", list.ToString());
    }

    [Fact]
    public void ToString_Empty_ReturnsEmptyBrackets()
    {
        Assert.Equal("[]", ZsList<int>.Empty.ToString());
    }

    [Fact]
    public void GetEnumerator_EnumeratesAllElements()
    {
        var list = ZsList<int>.FromItems(1, 2, 3);
        var collected = new List<int>();
        foreach (var item in list)
            collected.Add(item);
        Assert.Equal([1, 2, 3], collected);
    }

    [Fact]
    public void IsEmpty_NonEmptyList_ReturnsFalse()
    {
        var list = ZsList<int>.FromItems(1);
        Assert.False(list.IsEmpty);
    }

    [Fact]
    public void Of_HelperCreatesListCorrectly()
    {
        var list = ZsList.Of(1, 2, 3);
        Assert.Equal(3, list.Count);
        Assert.Equal(1, list[0]);
        Assert.Equal(2, list[1]);
        Assert.Equal(3, list[2]);
    }

    [Fact]
    public void Immutability_OriginalUnchangedAfterCons()
    {
        var original = ZsList<int>.FromItems(2, 3);
        _ = original.Cons(1);
        Assert.Equal(2, original.Count);
    }

    [Fact]
    public void Immutability_OriginalUnchangedAfterAppend()
    {
        var original = ZsList<int>.FromItems(1, 2);
        _ = original.Append(3);
        Assert.Equal(2, original.Count);
    }
}
