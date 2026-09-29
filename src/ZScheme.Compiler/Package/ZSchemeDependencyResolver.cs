using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ZScheme.Compiler.Cache;
using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Package;

public sealed class ZSchemeDependencyResolver(
    DiagnosticBag diagnostics,
    string manifestDirectory,
    string? cacheRoot = null
)
{
    private readonly string _cacheRoot = cacheRoot ?? ZSchemePaths.GetGitCacheRoot();

    public List<string> Resolve(IReadOnlyList<ZSchemeDependency> dependencies)
    {
        var paths = new List<string>();

        foreach (var dep in dependencies)
        {
            var path = dep.Source switch
            {
                ZSchemeDependencySource.Local local => ResolveLocal(local.Path, dep),
                ZSchemeDependencySource.Git git => ResolveGit(
                    git.Url,
                    git.VersionOrRef,
                    git.Subdirectory,
                    dep
                ),
                _ => null,
            };

            if (path is not null)
                paths.Add(path);
        }

        return paths;
    }

    private string? ResolveLocal(string relativePath, ZSchemeDependency dep)
    {
        var fullPath = Path.GetFullPath(Path.Combine(manifestDirectory, relativePath));

        if (!Directory.Exists(fullPath))
        {
            diagnostics.Error(
                $"Local dependency '{dep.Name}' path not found: {fullPath}",
                dep.Span
            );
            return null;
        }

        return fullPath;
    }

    private string? ResolveGit(
        string url,
        string versionOrRef,
        string? subdirectory,
        ZSchemeDependency dep
    )
    {
        var cacheDir = CloneGit(url, versionOrRef, dep);
        if (cacheDir is null)
            return null;

        // The repo root is only the package root when the manifest sits at the top of the
        // tree. A subdirectory names the package within a monorepo clone.
        if (subdirectory is not { Length: > 0 })
            return cacheDir;

        // Manifests are third-party input: a rooted subdirectory would make Path.Combine
        // discard the cache dir entirely, and ".." segments walk out of the clone.
        if (Path.IsPathRooted(subdirectory))
        {
            diagnostics.Error(
                $"Git dependency '{dep.Name}': subdirectory '{subdirectory}' must be a relative path",
                dep.Span
            );
            return null;
        }

        var full = Path.GetFullPath(
            Path.Combine(
                cacheDir,
                subdirectory
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar)
            )
        );
        var cacheRoot = Path.GetFullPath(cacheDir);
        if (
            full != cacheRoot
            && !full.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        )
        {
            diagnostics.Error(
                $"Git dependency '{dep.Name}': subdirectory '{subdirectory}' escapes the cloned repository",
                dep.Span
            );
            return null;
        }

        if (!Directory.Exists(full))
        {
            diagnostics.Error(
                $"Git dependency '{dep.Name}': subdirectory '{subdirectory}' not found in {url}@{versionOrRef}",
                dep.Span
            );
            return null;
        }

        return full;
    }

    private string? CloneGit(string url, string versionOrRef, ZSchemeDependency dep)
    {
        var urlHash = ComputeUrlHash(url);
        var cacheDir = Path.Combine(_cacheRoot, urlHash, versionOrRef);

        if (
            Directory.Exists(cacheDir)
            && Directory.GetFiles(cacheDir, "*.zs", SearchOption.AllDirectories).Length > 0
        )
            return cacheDir;

        Directory.CreateDirectory(Path.GetDirectoryName(cacheDir)!);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"clone --branch {versionOrRef} --depth 1 {url} \"{cacheDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                diagnostics.Error($"Failed to start git for dependency '{dep.Name}'", dep.Span);
                return null;
            }

            process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                diagnostics.Error(
                    $"Failed to clone dependency '{dep.Name}' from {url}@{versionOrRef}:\n{stderr}",
                    dep.Span
                );
                return null;
            }

            return cacheDir;
        }
        catch (Exception ex)
        {
            diagnostics.Error($"Failed to clone dependency '{dep.Name}': {ex.Message}", dep.Span);
            return null;
        }
    }

    private static string ComputeUrlHash(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
