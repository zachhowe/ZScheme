using System.Text;

namespace ZScheme.Compiler.Codegen;

public sealed record SolutionProjectEntry(string Folder, string RelativePath);

public static class CSharpSolutionGenerator
{
    public static string GenerateSlnx(IReadOnlyList<SolutionProjectEntry> projects)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Solution>");

        foreach (var group in projects.GroupBy(p => p.Folder))
        {
            sb.AppendLine($"    <Folder Name=\"/{group.Key}/\">");
            foreach (var entry in group)
                sb.AppendLine($"        <Project Path=\"{entry.RelativePath}\" />");
            sb.AppendLine("    </Folder>");
        }

        sb.Append("</Solution>");
        return sb.ToString();
    }

    public static void WriteSlnx(string outputPath, IReadOnlyList<SolutionProjectEntry> projects)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, GenerateSlnx(projects));
    }
}
