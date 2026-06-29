using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Xunit;
using ZScheme.Compiler.Codegen;
using ZScheme.Compiler.Package;
using ZScheme.Compiler.Pipeline;

namespace ZScheme.Compiler.Tests.Codegen;

public class CoverageTests
{
    private static string GetStdLibPath()
    {
        var dir = Path.GetDirectoryName(typeof(CoverageTests).Assembly.Location)!;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ZScheme.slnx")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "packages", "stdlib", "src");
    }

    // --- CoverageContract metadata round-trip -------------------------------------------------

    [Fact]
    public void Meta_RoundTrips()
    {
        var points = new List<CoveragePoint>
        {
            new("a.zs", 1, 2, 3, CoverageKind.Line, 0),
            new("a.zs", 5, 4, 6, CoverageKind.Branch, 0),
            new("a.zs", 5, 4, 6, CoverageKind.Branch, 1),
        };

        var parsed = CoverageContract.ParseMeta(CoverageContract.SerializeMeta(points));

        Assert.Equal(points, parsed);
    }

    [Fact]
    public void ParseMeta_EmptyOrNull_YieldsNoPoints()
    {
        Assert.Empty(CoverageContract.ParseMeta(""));
        Assert.Empty(CoverageContract.ParseMeta(null));
    }

    // --- Aggregator merge across snapshots ----------------------------------------------------

    [Fact]
    public void Aggregator_MergesBranchesAcrossSnapshots_AndKeepsUnreachedLineUncovered()
    {
        var points = new List<CoveragePoint>
        {
            new("a.zs", 1, 1, 1, CoverageKind.Line, 0), // function entry
            new("a.zs", 3, 7, 1, CoverageKind.Line, 0), // the `if` line
            new("a.zs", 3, 7, 1, CoverageKind.Branch, 0), // then
            new("a.zs", 3, 7, 1, CoverageKind.Branch, 1), // else
            new("a.zs", 9, 1, 1, CoverageKind.Line, 0), // never executed
        };

        var agg = new CoverageAggregator();
        // Snapshot from a test DLL that took the then-branch.
        agg.Add([1, 1, 1, 0, 0], points);
        // Snapshot from a test DLL that took the else-branch.
        agg.Add([1, 1, 0, 1, 0], points);

        var summary = agg.Summarize();
        Assert.Equal(2, summary.LinesCovered); // lines 1 and 3
        Assert.Equal(3, summary.LinesValid); // lines 1, 3, 9
        Assert.Equal(2, summary.BranchesCovered); // both then and else
        Assert.Equal(2, summary.BranchesValid);

        var report = agg.BuildReport("pkg", DateTimeOffset.UnixEpoch);
        var file = Assert.Single(report.Files);
        var unreached = file.Lines.Single(l => l.Number == 9);
        Assert.False(unreached.IsCovered);
        var ifLine = file.Lines.Single(l => l.Number == 3);
        Assert.Equal(2, ifLine.BranchesTotal);
        Assert.Equal(2, ifLine.BranchesTaken);
    }

    // --- Cobertura XML writer -----------------------------------------------------------------

    [Fact]
    public void CoberturaWriter_ProducesWellFormedXmlWithRatesAndBranches()
    {
        var root = Path.Combine(Path.GetTempPath(), "zscov_" + Guid.NewGuid().ToString("N"));
        var report = new CoverageReport(
            [
                new CoverageFile(
                    Path.Combine(root, "src", "a.zs"),
                    [
                        new CoverageLine(1, 2, 0, 0),
                        new CoverageLine(3, 2, 2, 2),
                        new CoverageLine(9, 0, 0, 0),
                    ]
                ),
                // A second file in a sibling directory forces the common source root up to
                // `root`, so filenames carry their subdirectory.
                new CoverageFile(Path.Combine(root, "lib", "b.zs"), [new CoverageLine(2, 1, 0, 0)]),
            ],
            "pkg",
            DateTimeOffset.UnixEpoch
        );

        var outPath = Path.Combine(root, "coverage.cobertura.xml");
        try
        {
            CoberturaWriter.Write(report, outPath);

            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
            using var reader = XmlReader.Create(outPath, settings);
            var doc = XDocument.Load(reader);

            var coverage = doc.Root!;
            Assert.Equal("coverage", coverage.Name.LocalName);
            Assert.Equal("4", coverage.Attribute("lines-valid")!.Value);
            Assert.Equal("3", coverage.Attribute("lines-covered")!.Value);
            Assert.Equal("2", coverage.Attribute("branches-valid")!.Value);
            Assert.Equal("2", coverage.Attribute("branches-covered")!.Value);

            // 3 of 4 lines covered.
            var lineRate = double.Parse(
                coverage.Attribute("line-rate")!.Value,
                System.Globalization.CultureInfo.InvariantCulture
            );
            Assert.Equal(0.75, lineRate, 3);

            var classes = coverage.Descendants("class").ToList();
            Assert.Equal(2, classes.Count);
            Assert.Contains(classes, c => c.Attribute("filename")!.Value == "src/a.zs");
            Assert.Contains(classes, c => c.Attribute("filename")!.Value == "lib/b.zs");

            var cls = classes.Single(c => c.Attribute("filename")!.Value == "src/a.zs");
            var lines = cls.Descendants("line").ToList();
            Assert.Equal(3, lines.Count);

            var branchLine = lines.Single(l => l.Attribute("number")!.Value == "3");
            Assert.Equal("true", branchLine.Attribute("branch")!.Value);
            Assert.Equal("100% (2/2)", branchLine.Attribute("condition-coverage")!.Value);

            var plainLine = lines.Single(l => l.Attribute("number")!.Value == "9");
            Assert.Equal("false", plainLine.Attribute("branch")!.Value);
            Assert.Equal("0", plainLine.Attribute("hits")!.Value);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    // --- End-to-end: instrumented IL records what runs ---------------------------------------

    [Fact]
    public void InstrumentedIl_RecordsExecutedPoints_AndUntakenBranchStaysZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zscov_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "covt.zs");
        const string source = """
            (module covt)
            (define (classify [n : Int]) : Int
              (if (< n 0) -1 1))
            (define (Compute) : Int
              (classify 7))
            """;

        var compilation = new Compilation(
            new CompilerOptions
            {
                OutputMode = OutputMode.Il,
                PackagePaths = new Dictionary<string, string> { ["stdlib"] = GetStdLibPath() },
                Coverage = new CoverageOptions { Enabled = true, IncludePathPrefixes = [dir] },
            }
        );

        var result = compilation.Compile(source, path);
        Assert.True(
            result.Success,
            "Compilation failed:\n" + string.Join("\n", result.Diagnostics.Diagnostics)
        );

        var il = (CompilationResult.IlOutputResult)result;
        var asm = Assembly.Load(il.OutputBytes);

        var compute = asm.GetExportedTypes()
            .SelectMany(t => t.GetMethods())
            .First(m =>
                m.Name.Equals("Compute", StringComparison.OrdinalIgnoreCase)
                && m.GetParameters().Length == 0
            );
        // Invoking also proves the instrumented IL is valid (a bad probe throws here).
        var value = (int)compute.Invoke(null, null)!;
        Assert.Equal(1, value); // classify(7): n<0 is false -> else branch -> 1

        var covType = asm.GetTypes().Single(t => t.Name == CoverageContract.TypeName);
        var hits = (int[])
            covType
                .GetField(CoverageContract.HitsField, BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!;
        var meta = (string)
            covType
                .GetField(CoverageContract.MetaField, BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!;
        var points = CoverageContract.ParseMeta(meta);

        Assert.Equal(points.Count, hits.Length);
        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.Equal(path, p.File));

        // Some line in the function ran.
        var lineIdxs = Enumerable
            .Range(0, points.Count)
            .Where(i => points[i].Kind == CoverageKind.Line)
            .ToList();
        Assert.Contains(lineIdxs, i => hits[i] > 0);

        // Exactly the else-branch should be taken; the then-branch must stay 0.
        var branchIdxs = Enumerable
            .Range(0, points.Count)
            .Where(i => points[i].Kind == CoverageKind.Branch)
            .ToList();
        Assert.True(branchIdxs.Count >= 2, "expected then+else branch points");
        Assert.Contains(branchIdxs, i => hits[i] > 0); // else taken
        Assert.Contains(branchIdxs, i => hits[i] == 0); // then never taken
    }
}
