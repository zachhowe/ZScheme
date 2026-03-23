namespace ZScript.Compiler.Package;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ZScript.Compiler.Diagnostics;

public sealed class NuGetResolver(DiagnosticBag diagnostics)
{
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zscript", "cache", "nuget");

    public string? Resolve(IReadOnlyList<NuGetDependency> packages)
    {
        if (packages.Count == 0)
            return null;

        var cacheKey = ComputeCacheKey(packages);
        var cacheDir = Path.Combine(CacheRoot, cacheKey);
        var outputDir = Path.Combine(cacheDir, "bin");

        if (Directory.Exists(outputDir) && Directory.GetFiles(outputDir, "*.dll").Length > 0)
            return outputDir;

        Directory.CreateDirectory(cacheDir);

        var csproj = GenerateCsproj(packages);
        var csprojPath = Path.Combine(cacheDir, "ZScriptDeps.csproj");
        File.WriteAllText(csprojPath, csproj);

        if (!RunDotnet($"restore \"{csprojPath}\"", cacheDir))
        {
            diagnostics.Error("Failed to restore NuGet packages", SourceSpan.None);
            return null;
        }

        if (!RunDotnet($"build \"{csprojPath}\" --no-restore -c Release -o \"{outputDir}\"", cacheDir))
        {
            diagnostics.Error("Failed to build NuGet dependency project", SourceSpan.None);
            return null;
        }

        return outputDir;
    }

    private static string GenerateCsproj(IReadOnlyList<NuGetDependency> packages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        sb.AppendLine("    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>");
        sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");
        foreach (var pkg in packages)
            sb.AppendLine($"    <PackageReference Include=\"{EscapeXml(pkg.PackageId)}\" Version=\"{EscapeXml(pkg.Version)}\" />");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");
        return sb.ToString();
    }

    private bool RunDotnet(string arguments, string workingDirectory)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                diagnostics.Error("Failed to start dotnet process", SourceSpan.None);
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                diagnostics.Error($"dotnet {arguments.Split(' ')[0]} failed:\n{stderr}", SourceSpan.None);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            diagnostics.Error($"Failed to run dotnet: {ex.Message}", SourceSpan.None);
            return false;
        }
    }

    private static string ComputeCacheKey(IReadOnlyList<NuGetDependency> packages)
    {
        var sorted = packages.OrderBy(p => p.PackageId).ThenBy(p => p.Version);
        var input = string.Join(";", sorted.Select(p => $"{p.PackageId}={p.Version}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
