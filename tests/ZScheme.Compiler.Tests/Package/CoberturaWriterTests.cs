using System.Xml;
using System.Xml.Linq;
using Xunit;
using ZScheme.Compiler.Package;

namespace ZScheme.Compiler.Tests.Package;

public class CoberturaWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"zs_cobertura_test_{Guid.NewGuid():N}"
    );

    public CoberturaWriterTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string OutPath(string name = "coverage.cobertura.xml") =>
        Path.Combine(_tempDir, name);

    private static XDocument Load(string path)
    {
        // The written file carries a Cobertura DOCTYPE; ignore the DTD rather than fetch it.
        using var reader = XmlReader.Create(
            path,
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }
        );
        return XDocument.Load(reader);
    }

    private string SrcFile(params string[] segments) =>
        Path.Combine([_tempDir, .. segments]);

    private static readonly DateTimeOffset Stamp = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CoverageTotalsMatchTheReport()
    {
        var report = new CoverageReport(
            [
                new CoverageFile(
                    SrcFile("src", "a.zs"),
                    [
                        new CoverageLine(1, 2, 0, 0),
                        new CoverageLine(2, 0, 0, 0),
                        new CoverageLine(3, 1, 1, 2),
                    ]
                ),
            ],
            "my-pkg",
            Stamp
        );
        var path = OutPath();
        CoberturaWriter.Write(report, path);

        var coverage = Load(path).Root!;
        Assert.Equal("3", coverage.Attribute("lines-valid")!.Value);
        Assert.Equal("2", coverage.Attribute("lines-covered")!.Value);
        Assert.Equal("2", coverage.Attribute("branches-valid")!.Value);
        Assert.Equal("1", coverage.Attribute("branches-covered")!.Value);
        // 2/3 with invariant '.' decimal separator.
        Assert.StartsWith("0.66", coverage.Attribute("line-rate")!.Value);
        Assert.Equal("0.5", coverage.Attribute("branch-rate")!.Value);
        Assert.Equal(
            Stamp.ToUnixTimeMilliseconds().ToString(),
            coverage.Attribute("timestamp")!.Value
        );
        Assert.Equal("my-pkg", coverage.Descendants("package").Single().Attribute("name")!.Value);
    }

    [Fact]
    public void EmptyReportHasRateOneAndEmptySourceRoot()
    {
        var report = new CoverageReport([], "empty-pkg", Stamp);
        var path = OutPath();
        CoberturaWriter.Write(report, path);

        var coverage = Load(path).Root!;
        Assert.Equal("1", coverage.Attribute("line-rate")!.Value);
        Assert.Equal("1", coverage.Attribute("branch-rate")!.Value);
        Assert.Equal("0", coverage.Attribute("lines-valid")!.Value);
        Assert.Equal("", coverage.Descendants("source").Single().Value);
    }

    [Fact]
    public void FilenamesAreRelativeToCommonRootWithForwardSlashes()
    {
        var report = new CoverageReport(
            [
                new CoverageFile(SrcFile("src", "sub1", "a.zs"), [new CoverageLine(1, 1, 0, 0)]),
                new CoverageFile(SrcFile("src", "sub2", "b.zs"), [new CoverageLine(1, 1, 0, 0)]),
            ],
            "pkg",
            Stamp
        );
        var path = OutPath();
        CoberturaWriter.Write(report, path);

        var doc = Load(path);
        var sourceRoot = doc.Descendants("source").Single().Value;
        Assert.Equal(Path.Combine(_tempDir, "src"), sourceRoot);

        var filenames = doc.Descendants("class")
            .Select(c => c.Attribute("filename")!.Value)
            .ToList();
        Assert.Equal(["sub1/a.zs", "sub2/b.zs"], filenames);
    }

    [Fact]
    public void BranchLinesCarryConditionCoverage()
    {
        var report = new CoverageReport(
            [
                new CoverageFile(
                    SrcFile("a.zs"),
                    [new CoverageLine(4, 1, 1, 2), new CoverageLine(9, 1, 0, 0)]
                ),
            ],
            "pkg",
            Stamp
        );
        var path = OutPath();
        CoberturaWriter.Write(report, path);

        var lines = Load(path).Descendants("line").ToList();
        var branchLine = lines.Single(l => l.Attribute("number")!.Value == "4");
        Assert.Equal("true", branchLine.Attribute("branch")!.Value);
        Assert.Equal("50% (1/2)", branchLine.Attribute("condition-coverage")!.Value);
        Assert.Single(branchLine.Descendants("condition"));

        var plainLine = lines.Single(l => l.Attribute("number")!.Value == "9");
        Assert.Equal("false", plainLine.Attribute("branch")!.Value);
        Assert.Null(plainLine.Attribute("condition-coverage"));
    }

    [Fact]
    public void LineHitsAreWrittenPerLine()
    {
        var report = new CoverageReport(
            [
                new CoverageFile(
                    SrcFile("a.zs"),
                    [new CoverageLine(1, 7, 0, 0), new CoverageLine(2, 0, 0, 0)]
                ),
            ],
            "pkg",
            Stamp
        );
        var path = OutPath();
        CoberturaWriter.Write(report, path);

        var lines = Load(path).Descendants("line").ToList();
        Assert.Equal("7", lines[0].Attribute("hits")!.Value);
        Assert.Equal("0", lines[1].Attribute("hits")!.Value);
    }

    [Fact]
    public void OutputDirectoryIsCreatedIfMissing()
    {
        var report = new CoverageReport(
            [new CoverageFile(SrcFile("a.zs"), [new CoverageLine(1, 1, 0, 0)])],
            "pkg",
            Stamp
        );
        var nested = Path.Combine(_tempDir, "does", "not", "exist", "coverage.xml");

        CoberturaWriter.Write(report, nested);

        Assert.True(File.Exists(nested));
    }
}
