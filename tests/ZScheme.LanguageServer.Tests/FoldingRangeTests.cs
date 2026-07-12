using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using ZScheme.LanguageServer.Handlers;

namespace ZScheme.LanguageServer.Tests;

public sealed class FoldingRangeTests
{
    [Fact]
    public void MultiLineForm_FoldsToClosingLine()
    {
        var source = "(define (add a b)\n  (+ a b))\n";
        var ranges = FoldingRangeHandler.Compute(source);

        var range = Assert.Single(ranges);
        Assert.Equal(0, range.StartLine);
        Assert.Equal(1, range.EndLine);
        Assert.Equal(FoldingRangeKind.Region, range.Kind);
    }

    [Fact]
    public void SingleLineForm_NoRange()
    {
        Assert.Empty(FoldingRangeHandler.Compute("(define x 1)\n"));
    }

    [Fact]
    public void NestedMultiLineForms_EachFold()
    {
        var source = "(define (f x)\n  (let ([y 1])\n    (+ x\n       y)))\n";
        var ranges = FoldingRangeHandler.Compute(source);

        // define (0..3), let (1..3), + (2..3)
        Assert.Equal(3, ranges.Count(r => r.Kind == FoldingRangeKind.Region));
        Assert.Contains(ranges, r => r.StartLine == 0 && r.EndLine == 3);
        Assert.Contains(ranges, r => r.StartLine == 1 && r.EndLine == 3);
        Assert.Contains(ranges, r => r.StartLine == 2 && r.EndLine == 3);
    }

    [Fact]
    public void CommentBlock_FoldsAsComment()
    {
        var source = "; line one\n; line two\n; line three\n(define x 1)\n";
        var ranges = FoldingRangeHandler.Compute(source);

        var comment = Assert.Single(ranges);
        Assert.Equal(FoldingRangeKind.Comment, comment.Kind);
        Assert.Equal(0, comment.StartLine);
        Assert.Equal(2, comment.EndLine);
    }

    [Fact]
    public void SingleComment_NoRange()
    {
        Assert.Empty(FoldingRangeHandler.Compute("; alone\n(define x 1)\n"));
    }

    [Fact]
    public void NonAdjacentComments_SeparateBlocks()
    {
        var source = "; a\n; b\n\n(define x 1)\n\n; c\n; d\n(define y 2)\n";
        var ranges = FoldingRangeHandler.Compute(source).Where(r => r.Kind == FoldingRangeKind.Comment).ToList();

        Assert.Equal(2, ranges.Count);
        Assert.Contains(ranges, r => r.StartLine == 0 && r.EndLine == 1);
        Assert.Contains(ranges, r => r.StartLine == 5 && r.EndLine == 6);
    }

    [Fact]
    public void TrailingComment_DoesNotJoinBlock()
    {
        var source = "(define x 1) ; trailing\n; leading\n(define y 2)\n";
        Assert.Empty(FoldingRangeHandler.Compute(source));
    }

    [Fact]
    public void UnbalancedSource_StillProducesRanges()
    {
        var source = "(define (f x)\n  (+ x\n";
        var ranges = FoldingRangeHandler.Compute(source);
        Assert.NotEmpty(ranges);
    }
}
