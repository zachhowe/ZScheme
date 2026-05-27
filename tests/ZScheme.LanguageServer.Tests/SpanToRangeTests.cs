using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Handlers;

namespace ZScheme.LanguageServer.Tests;

public sealed class SpanToRangeTests
{
    [Fact]
    public void SpanToRange_ConvertsOneBasedToZeroBased()
    {
        var span = new SourceSpan("f.zs", 5, 10, 3);
        var range = TextDocumentSyncHandler.SpanToRange(span);

        Assert.Equal(4, range.Start.Line);
        Assert.Equal(9, range.Start.Character);
    }

    [Fact]
    public void SpanToRange_EndColumnIsStartPlusLength()
    {
        var span = new SourceSpan("f.zs", 1, 1, 5);
        var range = TextDocumentSyncHandler.SpanToRange(span);

        Assert.Equal(0, range.End.Line);
        Assert.Equal(5, range.End.Character);
    }

    [Fact]
    public void SpanToRange_SingleCharSpan()
    {
        var span = new SourceSpan("f.zs", 3, 7, 1);
        var range = TextDocumentSyncHandler.SpanToRange(span);

        Assert.Equal(2, range.Start.Line);
        Assert.Equal(6, range.Start.Character);
        Assert.Equal(7, range.End.Character);
    }

    [Fact]
    public void SpanToRange_ZeroLengthSpan_StartEqualsEnd()
    {
        var span = new SourceSpan("f.zs", 2, 4, 0);
        var range = TextDocumentSyncHandler.SpanToRange(span);

        Assert.Equal(range.Start, range.End);
    }

    [Fact]
    public void SpanToRange_ZeroLineColumn_DoesNotGoNegative()
    {
        // SourceSpan.None uses (0, 0, 0); the handler clamps with Math.Max.
        var span = new SourceSpan("", 0, 0, 0);
        var range = TextDocumentSyncHandler.SpanToRange(span);

        Assert.Equal(0, range.Start.Line);
        Assert.Equal(0, range.Start.Character);
        Assert.Equal(0, range.End.Line);
        Assert.Equal(0, range.End.Character);
    }
}
