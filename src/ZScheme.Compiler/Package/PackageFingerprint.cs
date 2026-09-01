using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace ZScheme.Compiler.Package;

/// <summary>
///     A content hash of everything that goes into building a package, recorded alongside the
///     built artifact so a consumer can tell whether that artifact still matches the sources.
///     <para>
///         Modification times cannot answer this. The cache directory is keyed by package
///         <em>version</em> (<c>PackageCacheManager.GetPackageDir</c>), so editing a package
///         without bumping its manifest reuses the same entry, and a <c>git checkout</c> rewrites
///         the mtime of files whose content never changed — which under the test scripts'
///         parallel fan-out would mean every worker rebuilding every dependency at once.
///     </para>
///     <para>
///         The hash covers only the package's <em>own</em> inputs. A dependency's freshness is not
///         folded in: a consumer validates the whole closure by checking each package against its
///         own recorded fingerprint, so a change to stdlib invalidates stdlib's artifact directly
///         rather than through every artifact that happens to reference it.
///     </para>
/// </summary>
public static class PackageFingerprint
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(PackageFingerprint));

    /// <summary>
    ///     Hashes the package's manifest plus every <c>.zs</c> file under the source directory its
    ///     manifest names — the same set <c>LibraryCompiler.CompileModules</c> compiles, so a file
    ///     that cannot change the built assembly cannot change the fingerprint either. Returns
    ///     <c>null</c> when the inputs cannot be read, which callers treat as "cannot vouch for an
    ///     artifact" rather than as a match.
    /// </summary>
    public static string? Compute(string packageDir, PackageManifest manifest)
    {
        try
        {
            var sourceDir =
                manifest.Sources?.Main is { } main
                    ? Path.GetFullPath(Path.Combine(packageDir, main))
                    : packageDir;

            var inputs = new List<(string Key, string Path)>();

            var manifestPath = Path.Combine(packageDir, "package.zspkg");
            if (File.Exists(manifestPath))
                inputs.Add(("package.zspkg", manifestPath));

            if (Directory.Exists(sourceDir))
                foreach (var file in Directory.GetFiles(sourceDir, "*.zs", SearchOption.AllDirectories))
                    inputs.Add((
                        Path.GetRelativePath(sourceDir, file).Replace(Path.DirectorySeparatorChar, '/'),
                        file
                    ));

            if (inputs.Count == 0)
                return null;

            // Ordinal sort on the relative path so the hash does not depend on directory
            // enumeration order, which is not stable across file systems.
            inputs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (key, path) in inputs)
            {
                // The path is hashed too, so moving a file's content to a different module name
                // changes the fingerprint even though the bytes are unchanged.
                hash.AppendData(Encoding.UTF8.GetBytes(key));
                hash.AppendData([0]);
                hash.AppendData(File.ReadAllBytes(path));
                hash.AppendData([0]);
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(
                "PackageFingerprint: cannot fingerprint {PackageDir}: {Message}",
                packageDir,
                ex.Message
            );
            return null;
        }
    }
}
