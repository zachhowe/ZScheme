using ZScheme.Compiler.Codegen;

namespace ZScheme.Compiler.Package;

/// <summary>A single source line in the merged report.</summary>
public sealed record CoverageLine(int Number, long Hits, int BranchesTaken, int BranchesTotal)
{
    public bool IsCovered => Hits > 0;
    public bool HasBranches => BranchesTotal > 0;
}

/// <summary>One measured source file with its merged per-line coverage.</summary>
public sealed record CoverageFile(string AbsolutePath, IReadOnlyList<CoverageLine> Lines);

/// <summary>The fully merged coverage result, ready to serialize.</summary>
public sealed record CoverageReport(
    IReadOnlyList<CoverageFile> Files,
    string PackageName,
    DateTimeOffset Timestamp
);

/// <summary>Aggregate line/branch totals for a console summary.</summary>
public readonly record struct CoverageSummary(
    int LinesCovered,
    int LinesValid,
    int BranchesCovered,
    int BranchesValid
)
{
    public double LineRate => LinesValid == 0 ? 1.0 : (double)LinesCovered / LinesValid;
    public double BranchRate => BranchesValid == 0 ? 1.0 : (double)BranchesCovered / BranchesValid;
}

/// <summary>
///     Merges the per-assembly coverage snapshots read out of each test DLL's
///     <c>__ZSchemeCoverage</c> class. Because every test DLL re-emits the same code under test,
///     the same source line/branch appears in multiple snapshots; this collapses them so a line is
///     "covered" if it ran in <em>any</em> test assembly.
/// </summary>
public sealed class CoverageAggregator
{
    // file -> line -> summed hits (Line-kind points). Presence (even at 0 hits) makes the line valid.
    private readonly Dictionary<string, Dictionary<int, long>> _lineHits = new();

    // file -> distinct branch key (line, column, ordinal) -> summed hits. The key distinguishes
    // the then/else of one `if` (different ordinal) and unrelated branches on the same line
    // (different column), while merging the identical branch across test DLLs.
    private readonly Dictionary<
        string,
        Dictionary<(int Line, int Column, int Ordinal), long>
    > _branchHits = new();

    public bool HasData => _lineHits.Count > 0;

    public void Add(int[]? hits, IReadOnlyList<CoveragePoint> points)
    {
        if (hits is null)
            return;

        var count = Math.Min(hits.Length, points.Count);
        for (var i = 0; i < count; i++)
        {
            var p = points[i];
            long h = hits[i];
            if (p.Kind == CoverageKind.Branch)
            {
                if (!_branchHits.TryGetValue(p.File, out var byKey))
                    _branchHits[p.File] = byKey = new Dictionary<(int, int, int), long>();
                var key = (p.Line, p.Column, p.Ordinal);
                byKey[key] = byKey.GetValueOrDefault(key) + h;
            }
            else
            {
                if (!_lineHits.TryGetValue(p.File, out var byLine))
                    _lineHits[p.File] = byLine = new Dictionary<int, long>();
                byLine[p.Line] = byLine.GetValueOrDefault(p.Line) + h;
            }
        }
    }

    public CoverageReport BuildReport(string packageName, DateTimeOffset timestamp)
    {
        var files = new List<CoverageFile>();
        var allFiles = _lineHits
            .Keys.Union(_branchHits.Keys)
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in allFiles)
        {
            _lineHits.TryGetValue(file, out var byLine);
            _branchHits.TryGetValue(file, out var byBranch);

            var lineNumbers = new SortedSet<int>();
            if (byLine is not null)
                lineNumbers.UnionWith(byLine.Keys);
            if (byBranch is not null)
                lineNumbers.UnionWith(byBranch.Keys.Select(k => k.Line));

            var lines = new List<CoverageLine>();
            foreach (var n in lineNumbers)
            {
                var hits = byLine?.GetValueOrDefault(n) ?? 0;
                var branchesOnLine =
                    byBranch?.Where(kv => kv.Key.Line == n).Select(kv => kv.Value).ToList() ?? [];
                var total = branchesOnLine.Count;
                var taken = branchesOnLine.Count(v => v > 0);
                lines.Add(new CoverageLine(n, hits, taken, total));
            }

            files.Add(new CoverageFile(file, lines));
        }

        return new CoverageReport(files, packageName, timestamp);
    }

    public CoverageSummary Summarize()
    {
        var report = BuildReport("", DateTimeOffset.UnixEpoch);
        var allLines = report.Files.SelectMany(f => f.Lines).ToList();
        return new CoverageSummary(
            allLines.Count(l => l.IsCovered),
            allLines.Count,
            allLines.Sum(l => l.BranchesTaken),
            allLines.Sum(l => l.BranchesTotal)
        );
    }
}
