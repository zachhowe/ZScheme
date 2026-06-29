using System.Globalization;
using System.Xml;

namespace ZScheme.Compiler.Package;

/// <summary>
///     Serializes a merged <see cref="CoverageReport" /> to the standard Cobertura XML format
///     consumed by ReportGenerator, CI coverage gates, and IDE coverage views. Pure C#: no IL and
///     no runtime dependency on the instrumented assemblies.
/// </summary>
public static class CoberturaWriter
{
    public static void Write(CoverageReport report, string outputPath)
    {
        var sourceRoot = CommonRoot(report.Files.Select(f => f.AbsolutePath).ToList());

        var allLines = report.Files.SelectMany(f => f.Lines).ToList();
        var linesValid = allLines.Count;
        var linesCovered = allLines.Count(l => l.IsCovered);
        var branchesValid = allLines.Sum(l => l.BranchesTotal);
        var branchesCovered = allLines.Sum(l => l.BranchesTaken);

        var fullPath = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new System.Text.UTF8Encoding(false),
        };
        using var writer = XmlWriter.Create(fullPath, settings);

        writer.WriteStartDocument();
        writer.WriteDocType(
            "coverage",
            null,
            "http://cobertura.sourceforge.net/xml/coverage-04.dtd",
            null
        );

        writer.WriteStartElement("coverage");
        writer.WriteAttributeString("line-rate", Rate(linesCovered, linesValid));
        writer.WriteAttributeString("branch-rate", Rate(branchesCovered, branchesValid));
        writer.WriteAttributeString("lines-covered", Int(linesCovered));
        writer.WriteAttributeString("lines-valid", Int(linesValid));
        writer.WriteAttributeString("branches-covered", Int(branchesCovered));
        writer.WriteAttributeString("branches-valid", Int(branchesValid));
        writer.WriteAttributeString("complexity", "0");
        writer.WriteAttributeString("version", "0");
        writer.WriteAttributeString(
            "timestamp",
            report.Timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        );

        writer.WriteStartElement("sources");
        writer.WriteStartElement("source");
        writer.WriteString(sourceRoot);
        writer.WriteEndElement(); // source
        writer.WriteEndElement(); // sources

        writer.WriteStartElement("packages");
        writer.WriteStartElement("package");
        writer.WriteAttributeString("name", report.PackageName);
        writer.WriteAttributeString("line-rate", Rate(linesCovered, linesValid));
        writer.WriteAttributeString("branch-rate", Rate(branchesCovered, branchesValid));
        writer.WriteAttributeString("complexity", "0");

        writer.WriteStartElement("classes");
        foreach (var file in report.Files)
        {
            var rel = RelativePath(sourceRoot, file.AbsolutePath);
            var fileLinesValid = file.Lines.Count;
            var fileLinesCovered = file.Lines.Count(l => l.IsCovered);
            var fileBranchesValid = file.Lines.Sum(l => l.BranchesTotal);
            var fileBranchesCovered = file.Lines.Sum(l => l.BranchesTaken);

            writer.WriteStartElement("class");
            writer.WriteAttributeString("name", rel);
            writer.WriteAttributeString("filename", rel);
            writer.WriteAttributeString("line-rate", Rate(fileLinesCovered, fileLinesValid));
            writer.WriteAttributeString(
                "branch-rate",
                Rate(fileBranchesCovered, fileBranchesValid)
            );
            writer.WriteAttributeString("complexity", "0");

            writer.WriteStartElement("methods");
            writer.WriteEndElement(); // methods

            writer.WriteStartElement("lines");
            foreach (var line in file.Lines)
            {
                writer.WriteStartElement("line");
                writer.WriteAttributeString("number", Int(line.Number));
                writer.WriteAttributeString(
                    "hits",
                    line.Hits.ToString(CultureInfo.InvariantCulture)
                );
                writer.WriteAttributeString("branch", line.HasBranches ? "true" : "false");
                if (line.HasBranches)
                {
                    var pct = (int)Math.Round(100.0 * line.BranchesTaken / line.BranchesTotal);
                    writer.WriteAttributeString(
                        "condition-coverage",
                        $"{pct}% ({line.BranchesTaken}/{line.BranchesTotal})"
                    );
                    writer.WriteStartElement("conditions");
                    writer.WriteStartElement("condition");
                    writer.WriteAttributeString("number", "0");
                    writer.WriteAttributeString("type", "jump");
                    writer.WriteAttributeString("coverage", $"{pct}%");
                    writer.WriteEndElement(); // condition
                    writer.WriteEndElement(); // conditions
                }

                writer.WriteEndElement(); // line
            }

            writer.WriteEndElement(); // lines
            writer.WriteEndElement(); // class
        }

        writer.WriteEndElement(); // classes
        writer.WriteEndElement(); // package
        writer.WriteEndElement(); // packages
        writer.WriteEndElement(); // coverage
        writer.WriteEndDocument();
    }

    private static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Rate(int covered, int valid)
    {
        var rate = valid == 0 ? 1.0 : (double)covered / valid;
        return rate.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private static string RelativePath(string root, string file)
    {
        if (string.IsNullOrEmpty(root))
            return file.Replace('\\', '/');
        var rel = Path.GetRelativePath(root, file);
        return rel.Replace('\\', '/');
    }

    private static string CommonRoot(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return "";

        var dirSegments = files
            .Select(f => Path.GetDirectoryName(Path.GetFullPath(f)) ?? "")
            .Select(d => d.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();

        var common = dirSegments[0].ToList();
        foreach (var segs in dirSegments.Skip(1))
        {
            var k = 0;
            while (
                k < common.Count
                && k < segs.Length
                && string.Equals(common[k], segs[k], StringComparison.OrdinalIgnoreCase)
            )
                k++;
            common = common.Take(k).ToList();
        }

        return string.Join(Path.DirectorySeparatorChar, common);
    }
}
