using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Cli;

internal static class PackageCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: zs package <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  init    Initialize a new ZScheme package");
            return 0;
        }

        return args[0] switch
        {
            "init" => RunPackageInit(args[1..]),
            "--help" or "-h" => Run([]),
            _ => CliHelpers.Error($"Unknown package command: {args[0]}"),
        };
    }

    private static int RunPackageInit(string[] args)
    {
        string? name = null;
        var version = "0.1.0";
        string? importPrefix = null;
        string? description = null;
        string? license = null;
        var outputDir = ".";

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                case "--version" when i + 1 < args.Length:
                    version = args[++i];
                    break;
                case "--import-prefix" when i + 1 < args.Length:
                    importPrefix = args[++i];
                    break;
                case "--description" when i + 1 < args.Length:
                    description = args[++i];
                    break;
                case "--license" when i + 1 < args.Length:
                    license = args[++i];
                    break;
                case "--output" or "-o" when i + 1 < args.Length:
                    outputDir = args[++i];
                    break;
                default:
                    return CliHelpers.Error($"Unknown option: {args[i]}");
            }

        var fullOutputDir = Path.GetFullPath(outputDir);
        name ??= Path.GetFileName(fullOutputDir);
        importPrefix ??= name;

        var manifestPath = Path.Combine(fullOutputDir, "package.zspkg");
        if (File.Exists(manifestPath))
            return CliHelpers.Error($"Package already exists: {manifestPath}");

        // Build the namespace from the name (PascalCase, strip invalid chars)
        var ns = string.Concat(
            name.Split('-', '_', '.')
                .Where(s => s.Length > 0)
                .Select(s => char.ToUpperInvariant(s[0]) + s[1..])
        );
        if (ns.Length == 0)
            ns = "MyPackage";

        // Build manifest record and serialize
        var manifest = new PackageManifest(
            name,
            version,
            null,
            importPrefix,
            null,
            description,
            license,
            new PackageDependencies([], []),
            new PackageDependencies([], []),
            new BuildConfig(new MainBuildConfig(null, null, ns, []), null),
            new SourcePaths("src", "test"),
            SourceSpan.None
        );

        // Create directories
        Directory.CreateDirectory(fullOutputDir);
        var srcDir = Path.Combine(fullOutputDir, "src");
        var testDir = Path.Combine(fullOutputDir, "test");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(testDir);

        // Write manifest
        File.WriteAllText(manifestPath, ManifestSerializer.Serialize(manifest));
        Console.WriteLine($"Created: {manifestPath}");

        // Write hello-world main.zs
        var mainPath = Path.Combine(srcDir, "main.zs");
        var mainContent = $"""
            (define (main) : Unit
              (println "Hello from {name}!"))
            """;
        File.WriteAllText(mainPath, mainContent + Environment.NewLine);
        Console.WriteLine($"Created: {mainPath}");

        Console.WriteLine();
        Console.WriteLine($"Initialized package '{name}' in {fullOutputDir}");
        return 0;
    }
}
