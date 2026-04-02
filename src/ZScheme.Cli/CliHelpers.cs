using Serilog;
using ZScheme.Compiler.Diagnostics;
using ZScheme.Compiler.Package;

namespace ZScheme.Cli;

internal static class CliHelpers
{
    /// <summary>
    ///     Reads a package manifest from a directory to resolve the import prefix and source path.
    ///     Returns (importPrefix, sourceDir) or null if the manifest is missing or invalid.
    /// </summary>
    public static (string Prefix, string SourceDir, string? DefaultModule)? ResolvePackagePath(string packageDir)
    {
        Log.Debug("ResolvePackagePath: resolving {PackageDir}", packageDir);
        var fullDir = Path.GetFullPath(packageDir);
        var manifestPath = Path.Combine(fullDir, "package.zspkg");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"No package.zspkg found in: {fullDir}");
            return null;
        }

        var diag = new DiagnosticBag();
        var parser = new ManifestParser(diag);
        var manifest = parser.Parse(File.ReadAllText(manifestPath), manifestPath);
        if (manifest is null || diag.HasErrors)
        {
            foreach (var d in diag.Diagnostics)
                Console.Error.WriteLine(d);
            return null;
        }

        if (manifest.ImportPrefix is null)
        {
            Console.Error.WriteLine($"Package at '{fullDir}' has no (import-prefix ...) defined");
            return null;
        }

        var sourceDir = manifest.Sources?.Main is not null
            ? Path.GetFullPath(Path.Combine(fullDir, manifest.Sources.Main))
            : fullDir;

        Log.Debug("ResolvePackagePath: resolved prefix={Prefix}, sourceDir={SourceDir}, defaultModule={DefaultModule}",
            manifest.ImportPrefix, sourceDir, manifest.DefaultModule);
        return (manifest.ImportPrefix, sourceDir, manifest.DefaultModule);
    }

    public static void CopyPrecompiledAssemblies(IReadOnlyList<string> assemblyPaths, string outputDir)
    {
        foreach (var path in assemblyPaths)
        {
            var destPath = Path.Combine(outputDir, Path.GetFileName(path));
            if (path != destPath && File.Exists(path))
            {
                File.Copy(path, destPath, true);
                Console.WriteLine($"Copied: {Path.GetFileName(path)}");
            }
        }
    }

    public static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
