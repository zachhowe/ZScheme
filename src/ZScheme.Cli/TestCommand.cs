using Serilog;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Cli;

internal static class TestCommand
{
    public static int Run(string[] args)
    {
        string? manifestPath = null;
        var moduleSearchPaths = new List<string>();
        var assemblyRefPaths = new List<string>();
        var packagePaths = new Dictionary<string, string>();
        var moduleAliases = new Dictionary<string, string>();

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--manifest" or "-m" when i + 1 < args.Length:
                    manifestPath = args[++i];
                    break;
                case "--module-path" when i + 1 < args.Length:
                    moduleSearchPaths.Add(Path.GetFullPath(args[++i]));
                    break;
                case "--package-path" when i + 1 < args.Length:
                    var resolved = CliHelpers.ResolvePackagePath(args[++i]);
                    if (resolved is not null)
                    {
                        packagePaths[resolved.Value.Prefix] = resolved.Value.SourceDir;
                        if (resolved.Value.DefaultModule is { } defMod)
                            moduleAliases[resolved.Value.Prefix] = $"{resolved.Value.Prefix}/{defMod}";
                    }

                    break;
                case "--ref" when i + 1 < args.Length:
                    assemblyRefPaths.Add(Path.GetFullPath(args[++i]));
                    break;
            }

        Log.Debug("test: manifest={ManifestPath}, modulePaths={ModulePathCount}, packagePaths={PackagePathCount}",
            manifestPath ?? "(auto-detect)", moduleSearchPaths.Count, packagePaths.Count);

        // Find manifest if not specified
        if (manifestPath is null)
        {
            var candidates = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.zspkg");
            if (candidates.Length == 0)
            {
                Console.Error.WriteLine(
                    "No .zspkg manifest found in current directory. Use --manifest to specify one.");
                return 1;
            }

            if (candidates.Length > 1)
            {
                Console.Error.WriteLine("Multiple .zspkg files found. Use --manifest to specify one.");
                return 1;
            }

            manifestPath = candidates[0];
        }

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Manifest not found: {manifestPath}");
            return 1;
        }

        var diagnostics = new DiagnosticBag();
        var tester = new PackageTester(diagnostics);
        var result = tester.Test(manifestPath, moduleSearchPaths, assemblyRefPaths, packagePaths, moduleAliases);

        if (result is null)
        {
            foreach (var diag in diagnostics.Diagnostics)
                Console.Error.WriteLine(diag);
            return 1;
        }

        foreach (var testCase in result.Results)
            switch (testCase.Outcome)
            {
                case TestOutcome.Passed:
                    Console.WriteLine($"  PASS: {testCase.TestName}");
                    break;
                case TestOutcome.Failed:
                    Console.Error.WriteLine($"  FAIL: {testCase.TestName}");
                    if (testCase.FailureMessage is not null)
                        Console.Error.WriteLine($"        {testCase.FailureMessage}");
                    break;
            }

        Console.WriteLine(
            $"\nTests: {result.Passed} passed, {result.Failed} failed{(result.Skipped > 0 ? $", {result.Skipped} skipped" : "")} ({result.Total} total)");
        return result.Failed > 0 ? 1 : 0;
    }
}
