using ZScheme.Compiler.Codegen;

namespace ZScheme.Cli;

internal static class GenerateProjectCommand
{
    public static int Run(string[] args)
    {
        var outputDir = "output";
        string? projectOutputType = null;
        string? langVersion = null;
        var nugetPackages = new List<(string PackageId, string Version)>();

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--output" or "-o" when i + 1 < args.Length:
                    outputDir = args[++i];
                    break;
                case "--output-type" when i + 1 < args.Length:
                    projectOutputType = args[++i];
                    break;
                case "--lang-version" when i + 1 < args.Length:
                    langVersion = args[++i];
                    break;
                case "--nuget" when i + 1 < args.Length:
                {
                    var parts = args[++i].Split(':', 2);
                    if (parts.Length == 2)
                        nugetPackages.Add((parts[0], parts[1]));
                    else
                        Console.Error.WriteLine($"Invalid --nuget format: {args[i]} (expected PackageId:Version)");
                    break;
                }
            }

        var fullOutputDir = Path.GetFullPath(outputDir);
        var projectName = Path.GetFileName(fullOutputDir);
        var options = new CSharpProjectOptions
        {
            OutputType = projectOutputType ?? "Exe",
            LangVersion = langVersion,
            NuGetPackages = nugetPackages
        };

        Directory.CreateDirectory(fullOutputDir);
        var csprojPath = Path.Combine(fullOutputDir, $"{projectName}.csproj");
        File.WriteAllText(csprojPath, CSharpProjectGenerator.GenerateCsproj(options));
        Console.WriteLine($"Generated: {csprojPath}");
        return 0;
    }
}
