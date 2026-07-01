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
                ZSchemeDependencySource.Git git => ResolveGit(git.Url, git.VersionOrRef, dep),
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

    private string? ResolveGit(string url, string versionOrRef, ZSchemeDependency dep)
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
