using System.Text;

namespace ZScheme.Compiler.Codegen;

/// <summary>
///     Distinguishes the kind of a coverage point. A <see cref="Line" /> point marks an
///     executable statement/expression; a <see cref="Branch" /> point marks one outcome of a
///     decision (an <c>if</c> then/else, or a <c>match</c> arm) and is grouped per source line
///     to derive condition coverage.
/// </summary>
public enum CoverageKind
{
    Line,
    Branch,
}

/// <summary>
///     A single instrumented location, identified by its source span. The list of points
///     (index = point id) is serialized into the output assembly's <c>__ZSchemeCoverage.Meta</c>
///     field; the parallel <c>__ZSchemeCoverage.Hits</c> int[] records how many times each id ran.
/// </summary>
public readonly record struct CoveragePoint(
    string File,
    int Line,
    int Column,
    int Length,
    CoverageKind Kind,
    int Ordinal
);

/// <summary>
///     The contract shared by the IL emitter (which bakes the coverage support class into each
///     output assembly) and the test runner (which reads it back via reflection). Keeping the
///     names and the Meta wire format here ensures the two sides cannot drift.
/// </summary>
public static class CoverageContract
{
    /// <summary>Top-level type name synthesized into every instrumented assembly.</summary>
    public const string TypeName = "__ZSchemeCoverage";

    /// <summary><c>public static int[]</c> — hit counts, indexed by coverage-point id.</summary>
    public const string HitsField = "Hits";

    /// <summary><c>public static string</c> — packed coverage-point metadata (see below).</summary>
    public const string MetaField = "Meta";

    /// <summary>The probe method: <c>public static void Hit(int id)</c>.</summary>
    public const string HitMethod = "Hit";

    // Wire format: one point per line, tab-separated "file\tline\tcol\tlen\tkind\tordinal".
    private const char FieldSep = '\t';
    private const char RecordSep = '\n';

    public static string SerializeMeta(IReadOnlyList<CoveragePoint> points)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (i > 0)
                sb.Append(RecordSep);
            sb.Append(p.File)
                .Append(FieldSep)
                .Append(p.Line)
                .Append(FieldSep)
                .Append(p.Column)
                .Append(FieldSep)
                .Append(p.Length)
                .Append(FieldSep)
                .Append(p.Kind == CoverageKind.Branch ? "branch" : "line")
                .Append(FieldSep)
                .Append(p.Ordinal);
        }

        return sb.ToString();
    }

    public static IReadOnlyList<CoveragePoint> ParseMeta(string? meta)
    {
        if (string.IsNullOrEmpty(meta))
            return [];

        var result = new List<CoveragePoint>();
        foreach (var record in meta.Split(RecordSep))
        {
            if (record.Length == 0)
                continue;
            var fields = record.Split(FieldSep);
            if (fields.Length < 6)
                continue;
            result.Add(
                new CoveragePoint(
                    fields[0],
                    int.Parse(fields[1]),
                    int.Parse(fields[2]),
                    int.Parse(fields[3]),
                    fields[4] == "branch" ? CoverageKind.Branch : CoverageKind.Line,
                    int.Parse(fields[5])
                )
            );
        }

        return result;
    }
}
