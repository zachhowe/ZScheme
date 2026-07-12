using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

public class CoverageAggregatorTests
{
    private static CoveragePoint Line(string file, int line)
    {
        return new CoveragePoint(file, line, 0, 1, CoverageKind.Line, 0);
    }

    private static CoveragePoint Branch(string file, int line, int column, int ordinal)
    {
        return new CoveragePoint(file, line, column, 1, CoverageKind.Branch, ordinal);
    }

    [Fact]
    public void AddWithNullHitsIsNoOp()
    {
        var agg = new CoverageAggregator();
        agg.Add(null, [Line("a.zs", 1)]);

        Assert.False(agg.HasData);
        Assert.Empty(agg.BuildReport("pkg", DateTimeOffset.UnixEpoch).Files);
    }

    [Fact]
    public void ZeroHitLineAppearsAsUncovered()
    {
        var agg = new CoverageAggregator();
        agg.Add([0], [Line("a.zs", 3)]);

        var file = Assert.Single(agg.BuildReport("pkg", DateTimeOffset.UnixEpoch).Files);
        var line = Assert.Single(file.Lines);
        Assert.Equal(3, line.Number);
        Assert.Equal(0, line.Hits);
        Assert.False(line.IsCovered);
    }

    [Fact]
    public void HitsAreSummedAcrossAddCalls()
    {
        var agg = new CoverageAggregator();
        agg.Add([2], [Line("a.zs", 1)]);
        agg.Add([3], [Line("a.zs", 1)]);

        var line = Assert.Single(
            Assert.Single(agg.BuildReport("pkg", DateTimeOffset.UnixEpoch).Files).Lines
        );
        Assert.Equal(5, line.Hits);
    }

    [Fact]
    public void LengthMismatchIsTruncatedToShorter()
    {
        var agg = new CoverageAggregator();
        // Two hit slots but only one point: the extra hit is ignored.
        agg.Add([1, 9], [Line("a.zs", 1)]);
        // Two points but only one hit slot: the second point is ignored.
        agg.Add([1], [Line("b.zs", 1), Line("b.zs", 2)]);

        var report = agg.BuildReport("pkg", DateTimeOffset.UnixEpoch);
        Assert.Equal(2, report.Files.Count);
        Assert.Single(report.Files.First(f => f.AbsolutePath == "b.zs").Lines);
    }

    [Fact]
    public void BranchKeysDistinguishOutcomesOnSameLine()
    {
        var agg = new CoverageAggregator();
        // then-branch (ordinal 0) ran, else-branch (ordinal 1) did not.
        agg.Add([1, 0], [Branch("a.zs", 4, 2, 0), Branch("a.zs", 4, 2, 1)]);

        var line = Assert.Single(
            Assert.Single(agg.BuildReport("pkg", DateTimeOffset.UnixEpoch).Files).Lines
        );
        Assert.Equal(2, line.BranchesTotal);
        Assert.Equal(1, line.BranchesTaken);
        Assert.True(line.HasBranches);
    }

    [Fact]
    public void SameBranchAcrossAddCallsIsMergedNotDuplicated()
    {
        var agg = new CoverageAggregator();
        agg.Add([0], [Branch("a.zs", 4, 2, 0)]);
        agg.Add([1], [Branch("a.zs", 4, 2, 0)]);

        var line = Assert.Single(
            Assert.Single(agg.BuildReport("pkg", DateTimeOffset.UnixEpoch).Files).Lines
        );
        Assert.Equal(1, line.BranchesTotal);
        Assert.Equal(1, line.BranchesTaken);
    }

    [Fact]
    public void BranchOnlyLineHasZeroLineHits()
    {
        var agg = new CoverageAggregator();
        agg.Add([1], [Branch("a.zs", 7, 0, 0)]);

        var line = Assert.Single(
            Assert.Single(agg.BuildReport("pkg", DateTimeOffset.UnixEpoch).Files).Lines
        );
        Assert.Equal(7, line.Number);
        Assert.Equal(0, line.Hits);
        Assert.Equal(1, line.BranchesTaken);
    }

    [Fact]
    public void FilesAreOrdinalOrderedAndLinesSorted()
    {
        var agg = new CoverageAggregator();
        agg.Add([1, 1], [Line("b.zs", 9), Line("b.zs", 2)]);
        agg.Add([1], [Line("a.zs", 1)]);

        var report = agg.BuildReport("pkg", DateTimeOffset.UnixEpoch);
        Assert.Equal(["a.zs", "b.zs"], report.Files.Select(f => f.AbsolutePath));
        Assert.Equal([2, 9], report.Files[1].Lines.Select(l => l.Number));
    }

    [Fact]
    public void ReportCarriesPackageNameAndTimestamp()
    {
        var agg = new CoverageAggregator();
        agg.Add([1], [Line("a.zs", 1)]);
        var stamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var report = agg.BuildReport("my-pkg", stamp);
        Assert.Equal("my-pkg", report.PackageName);
        Assert.Equal(stamp, report.Timestamp);
    }

    [Fact]
    public void SummarizeMatchesBuildReportTotals()
    {
        var agg = new CoverageAggregator();
        agg.Add(
            [1, 0, 1, 0],
            [
                Line("a.zs", 1),
                Line("a.zs", 2),
                Branch("a.zs", 1, 0, 0),
                Branch("a.zs", 1, 0, 1),
            ]
        );

        var summary = agg.Summarize();
        Assert.Equal(1, summary.LinesCovered);
        Assert.Equal(2, summary.LinesValid);
        Assert.Equal(1, summary.BranchesCovered);
        Assert.Equal(2, summary.BranchesValid);
        Assert.Equal(0.5, summary.LineRate);
        Assert.Equal(0.5, summary.BranchRate);
    }

    [Fact]
    public void EmptySummaryRatesAreOne()
    {
        var summary = new CoverageAggregator().Summarize();
        Assert.Equal(1.0, summary.LineRate);
        Assert.Equal(1.0, summary.BranchRate);
    }
}
