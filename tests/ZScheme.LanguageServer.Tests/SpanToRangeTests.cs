using Xunit;
using ZScheme.Compiler.Diagnostics;
using ZScheme.LanguageServer.Handlers;

namespace ZScheme.LanguageServer.Tests;

public sealed class SpanToRangeTests
{
    [Fact]
    public void SpanToRange_ConvertsOneBasedToZeroBased()
    {
        var span = new SourceSpan("f.zs", Line: 5, Column: 10, Length: 3);
        var range = TextDocumentSyncHandler.SpanToRange(span);

        Assert.Equal(4, range.Start.Line);
        Assert.Equal(9, range.Start.Character);
    }

    [Fact]
    public void SpanToRange_EndColumnIsStartPlusLength()
    {
        var span = new SourceSpan("f.zs", Line: 1, Column: 1, Length: 5);
        var range = TextDocumentSyncHandler.SpanToRange(span);

        Assert.Equal(0, range.End.Line);
        Assert.Equal(5, range.End.Character);
    }

    [Fact]
    public void SpanToRange_SingleCharSpan()
    {
        var span = new SourceSpan("f.zs", Line: 3, Column: 7, Length: 1);
        var range = TextDocumentSyncHandler.SpanToRange(span);

        Assert.Equal(2, range.Start.Line);
        Assert.Equal(6, range.Start.Character);
        Assert.Equal(7, range.End.Character);
    }

    [Fact]
    public void SpanToRange_ZeroLengthSpan_StartEqualsEnd()
    {
        var span = new SourceSpan("f.zs", Line: 2, Column: 4, Length: 0);
        var range = TextDocumentSyncHandler.SpanToRange(span);

        Assert.Equal(range.Start, range.End);
    }

    [Fact]
    public void SpanToRange_ZeroLineColumn_DoesNotGoNegative()
    {
        // SourceSpan.None uses (0, 0, 0); the handler clamps with Math.Max.
        var span = new SourceSpan("", Line: 0, Column: 0, Length: 0);
        var range = TextDocumentSyncHandler.SpanToRange(span);

        Assert.Equal(0, range.Start.Line);
        Assert.Equal(0, range.Start.Character);
        Assert.Equal(0, range.End.Line);
        Assert.Equal(0, range.End.Character);
    }
}
