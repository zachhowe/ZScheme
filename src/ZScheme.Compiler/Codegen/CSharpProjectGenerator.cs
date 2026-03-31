using System.Text;

namespace ZScheme.Compiler.Codegen;

public sealed record CSharpProjectOptions
{
    public string OutputType { get; init; } = "Exe";
    public string? LangVersion { get; init; }
    public IReadOnlyList<string> AssemblyReferences { get; init; } = [];
    public IReadOnlyList<(string PackageId, string Version)> NuGetPackages { get; init; } = [];
}

public static class CSharpProjectGenerator
{
    public static string GenerateCsproj(CSharpProjectOptions options)
    {
        var version = Environment.Version;
        var sb = new StringBuilder();

        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <OutputType>{options.OutputType}</OutputType>");
        sb.AppendLine($"    <TargetFramework>net{version.Major}.{version.Minor}</TargetFramework>");
        sb.AppendLine("    <Nullable>enable</Nullable>");

        if (options.LangVersion is not null)
            sb.AppendLine($"    <LangVersion>{options.LangVersion}</LangVersion>");

        if (options.AssemblyReferences.Count > 0 || options.NuGetPackages.Count > 0)
            sb.AppendLine("    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>");

        sb.AppendLine("  </PropertyGroup>");

        if (options.AssemblyReferences.Count > 0 || options.NuGetPackages.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");

            foreach (var path in options.AssemblyReferences)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                sb.AppendLine($"    <Reference Include=\"{name}\">");
                sb.AppendLine($"      <HintPath>{path}</HintPath>");
                sb.AppendLine("    </Reference>");
            }

            foreach (var (packageId, packageVersion) in options.NuGetPackages)
                sb.AppendLine($"    <PackageReference Include=\"{packageId}\" Version=\"{packageVersion}\" />");

            sb.AppendLine("  </ItemGroup>");
        }

        sb.Append("</Project>");
        return sb.ToString();
    }

    public static void WriteProjectDirectory(
        string outputDir,
        string projectName,
        IReadOnlyList<(string FileName, string Content)> csFiles,
        CSharpProjectOptions options)
    {
        Directory.CreateDirectory(outputDir);

        var csprojPath = Path.Combine(outputDir, $"{projectName}.csproj");
        File.WriteAllText(csprojPath, GenerateCsproj(options));

        foreach (var (fileName, content) in csFiles)
        {
            var filePath = Path.Combine(outputDir, fileName);
            File.WriteAllText(filePath, content);
        }
    }
}
