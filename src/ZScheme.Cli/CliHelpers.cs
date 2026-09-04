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
    public static (string Prefix, string SourceDir, string? DefaultModule)? ResolvePackagePath(
        string packageDir
    )
    {
        Log.Debug("ResolvePackagePath: resolving {PackageDir}", packageDir);
        var fullDir = Path.GetFullPath(packageDir);
        if (!File.Exists(Path.Combine(fullDir, "package.zspkg")))
        {
            Console.Error.WriteLine($"No package.zspkg found in: {fullDir}");
            return null;
        }

        var resolved = PackageDependencyResolver.TryResolvePackage(fullDir);
        if (resolved is null)
        {
            // package.zspkg exists but is unusable: parse error or missing import-prefix.
            // An explicit --package-path expects a real prefixed package, so report it.
            Console.Error.WriteLine($"Package at '{fullDir}' has no (import-prefix ...) defined");
            return null;
        }

        Log.Debug(
            "ResolvePackagePath: resolved prefix={Prefix}, sourceDir={SourceDir}, defaultModule={DefaultModule}",
            resolved.Prefix,
            resolved.SourceDir,
            resolved.DefaultModule
        );
        return (resolved.Prefix, resolved.SourceDir, resolved.DefaultModule);
    }

    public static void CopyPrecompiledAssemblies(
        IReadOnlyList<string> assemblyPaths,
        string outputDir
    )
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

    /// <summary>
    ///     Whether <paramref name="ex" /> is the filesystem refusing a write into the output
    ///     directory — a locked or read-only file, a directory that cannot be created — as
    ///     opposed to a bug. Its message names the path, so it is reported as an error the
    ///     user can act on rather than an unhandled-exception stack trace.
    /// </summary>
    public static bool IsOutputFailure(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException;
    }

    public static IReadOnlyList<string> ResolveFrameworkRefDirs(
        IReadOnlyList<FrameworkDependency> frameworks,
        DiagnosticBag diagnostics
    )
    {
        return FrameworkResolver.Resolve(frameworks, diagnostics);
    }
}
