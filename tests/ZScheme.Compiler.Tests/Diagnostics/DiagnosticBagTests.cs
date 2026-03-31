using Xunit;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Tests.Diagnostics;

public class DiagnosticBagTests
{
    [Fact]
    public void EmptyBag_HasNoErrors()
    {
        var bag = new DiagnosticBag();
        Assert.False(bag.HasErrors);
        Assert.Empty(bag.Diagnostics);
    }

    [Fact]
    public void Error_SetsHasErrors()
    {
        var bag = new DiagnosticBag();
        bag.Error("something went wrong", SourceSpan.None);
        Assert.True(bag.HasErrors);
        Assert.Single(bag.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, bag.Diagnostics[0].Severity);
    }

    [Fact]
    public void Warning_DoesNotSetHasErrors()
    {
        var bag = new DiagnosticBag();
        bag.Warning("watch out", SourceSpan.None);
        Assert.False(bag.HasErrors);
        Assert.Single(bag.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, bag.Diagnostics[0].Severity);
    }

    [Fact]
    public void AddRange_MergesDiagnostics()
    {
        var bag1 = new DiagnosticBag();
        bag1.Error("err1", SourceSpan.None);

        var bag2 = new DiagnosticBag();
        bag2.Warning("warn1", SourceSpan.None);
        bag2.Error("err2", SourceSpan.None);

        bag1.AddRange(bag2);

        Assert.Equal(3, bag1.Diagnostics.Count);
        Assert.True(bag1.HasErrors);
    }

    [Fact]
    public void Diagnostics_ContainCorrectSeverityMessageSpan()
    {
        var bag = new DiagnosticBag();
        var span = new SourceSpan("test.zs", 5, 10, 3);
        bag.Report(DiagnosticSeverity.Error, "test message", span);

        var d = Assert.Single(bag.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("test message", d.Message);
        Assert.Equal(span, d.Span);
        Assert.True(d.IsError);
    }
}
