using System.Text.Json;

namespace ZScheme.Toolchain;

/// <summary>Installs a toolchain into <c>~/.zscheme/toolchains/&lt;name&gt;</c>.</summary>
public sealed class ToolchainInstaller(string? home = null)
{
    /// <summary>
    ///     How old a leftover staging/trash directory must be before it is swept. Long enough that a
    ///     concurrently running install is never mistaken for debris.
    /// </summary>
    private static readonly TimeSpan TransientMaxAge = TimeSpan.FromHours(6);

    private readonly string _home = ZSchemeHome.GetHome(home);

    /// <param name="Name">Toolchain name, normally the version.</param>
    /// <param name="Dir">Where it was installed.</param>
    /// <param name="PackagesSeeded">Package versions copied into the shared cache.</param>
    /// <param name="Warnings">
    ///     Best-effort steps that failed after the install had already committed. The install
    ///     succeeded; these are for the caller to report, not to fail on.
    /// </param>
    public sealed record Result(
        string Name,
        string Dir,
        int PackagesSeeded,
        IReadOnlyList<string> Warnings
    );

    /// <summary>
    ///     Installs from a local archive or directory.
    /// </summary>
    /// <remarks>
    ///     Staging happens under the home rather than the system temp directory, because the commit
    ///     step is a directory rename into <c>toolchains/</c> and that requires the same volume.
    /// </remarks>
    /// <param name="force">
    ///     Replace an existing installation of the same name, or a linked toolchain of that name.
    /// </param>
    public Result InstallFrom(string source, string name, bool force = false, string? sha256 = null)
    {
        ToolchainName.Validate(name);

        var destDir = ZSchemeHome.GetToolchainDir(name, _home);
        if (Directory.Exists(destDir) && !force)
            throw new IOException(
                $"toolchain '{name}' is already installed; pass --force to replace it"
            );

        // The reciprocal of the guard in ToolchainRegistry.Link. Without it a name can end up with
        // both a directory and a .link file, which List reports twice and `zsup uninstall` only
        // half removes -- leaving the stale link as the toolchain that name now selects.
        var linkFile = ZSchemeHome.GetToolchainLinkFile(name, _home);
        if (File.Exists(linkFile) && !force)
            throw new IOException(
                $"'{name}' is a linked toolchain; run `zsup unlink {name}` first, or pass --force to replace it"
            );

        var downloads = ZSchemeHome.GetDownloadsDir(_home);
        Directory.CreateDirectory(downloads);
        SweepTransients(downloads);

        var staging = Path.Combine(downloads, ".staging-" + Guid.NewGuid().ToString("N")[..12]);
        string? trash = null;

        try
        {
            if (Directory.Exists(source))
                ArchiveExtractor.CopyDirectory(source, staging);
            else if (File.Exists(source))
                ArchiveExtractor.Extract(source, staging);
            else
                throw new FileNotFoundException($"No such archive or directory: {source}", source);

            NormalizeLayout(staging);
            MarkExecutables(staging);
            WriteMetadata(staging, name, source, sha256);

            Directory.CreateDirectory(ZSchemeHome.GetToolchainsDir(_home));

            if (Directory.Exists(destDir))
            {
                // Moved aside rather than deleted, so a failure part-way through the rename does
                // not leave the user with neither the old nor the new toolchain.
                trash = Path.Combine(downloads, ".trash-" + Guid.NewGuid().ToString("N")[..12]);
                Directory.Move(destDir, trash);
            }

            // The commit point.
            Directory.Move(staging, destDir);
        }
        catch
        {
            TryDelete(staging);
            // Put the previous installation back if we had already moved it aside.
            if (trash is not null && Directory.Exists(trash) && !Directory.Exists(destDir))
                Directory.Move(trash, destDir);
            throw;
        }

        if (trash is not null)
            TryDelete(trash);

        // Past the commit point everything is best-effort. Letting a failure here propagate would
        // report an install that did happen as a failure, and the caller would then skip stamping
        // the shims and setting the default -- the worst of both outcomes.
        var warnings = new List<string>();
        var seeded = 0;

        try
        {
            seeded = PackageCacheSeeder.Seed(destDir, _home, force);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"could not seed the prebuilt package cache: {e.Message}");
        }

        // Only reachable under --force; the check above rejects the collision otherwise.
        try
        {
            if (File.Exists(linkFile))
                File.Delete(linkFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add(
                $"'{name}' still has a link file at {linkFile}; remove it with `zsup unlink {name}`"
            );
        }

        return new Result(name, destDir, seeded, warnings);
    }

    /// <summary>
    ///     Ensures the binaries live in <c>bin/</c>. Archives produced before the layout change —
    ///     and dev staging directories — put them at the root instead.
    /// </summary>
    private static void NormalizeLayout(string staging)
    {
        var binDir = Path.Combine(staging, "bin");
        if (File.Exists(Path.Combine(binDir, ZSchemeHome.ExeName("zs"))))
            return;

        if (!File.Exists(Path.Combine(staging, ZSchemeHome.ExeName("zs"))))
            throw new InvalidOperationException(
                $"the archive does not contain {ZSchemeHome.ExeName("zs")}; it does not look like a ZScheme toolchain"
            );

        // Move everything except the sibling payload directories down into bin/.
        var keepAtRoot = new[] { "packages", PackageCacheSeeder.DirectoryName };
        var temp = Path.Combine(staging, ".bin-" + Guid.NewGuid().ToString("N")[..8]);

        // Materialized before the loop: enumeration is lazy, and moving entries out of the
        // directory being walked can silently skip entries on both NTFS and ext4.
        var entries = Directory.GetFileSystemEntries(staging);
        Directory.CreateDirectory(temp);

        foreach (var entry in entries)
        {
            var entryName = Path.GetFileName(entry);
            if (entryName == Path.GetFileName(temp) || keepAtRoot.Contains(entryName))
                continue;

            var target = Path.Combine(temp, entryName);
            if (Directory.Exists(entry))
                Directory.Move(entry, target);
            else
                File.Move(entry, target);
        }

        Directory.Move(temp, binDir);
    }

    /// <summary>
    ///     Forces mode 0755 on the executables.
    /// </summary>
    /// <remarks>
    ///     Not redundant with what the archive carries: a <c>.tar.gz</c> built on Windows records
    ///     mode 0644, so an extracted <c>zs</c> would otherwise not be runnable.
    /// </remarks>
    private static void MarkExecutables(string staging)
    {
        if (OperatingSystem.IsWindows())
            return;

        var binDir = Path.Combine(staging, "bin");
        foreach (var name in ShimInstaller.ShimNames)
        {
            var path = Path.Combine(binDir, name);
            if (File.Exists(path))
                ShimInstaller.MakeExecutable(path);
        }
    }

    private static void WriteMetadata(string staging, string name, string source, string? sha256)
    {
        var metadata = new ToolchainMetadata
        {
            Name = name,
            // The compiler version the payload was built as, which is what keys the package cache.
            // It is not the same as the install name: `zsup install dev --from <0.4.0 archive>`.
            Version = PackageCacheSeeder.FindCompilerVersion(staging) ?? name,
            Rid = RuntimeIdentifier.TryDetect(),
            InstalledAt = DateTimeOffset.UtcNow.ToString("O"),
            Source = source,
            Sha256 = sha256,
        };

        File.WriteAllText(
            Path.Combine(staging, "toolchain.json"),
            JsonSerializer.Serialize(metadata, ToolchainJsonContext.Default.ToolchainMetadata)
                + Environment.NewLine
        );
    }

    /// <summary>
    ///     Removes staging and trash directories left behind by an interrupted install.
    /// </summary>
    /// <remarks>
    ///     Only entries older than <see cref="TransientMaxAge" /> are swept. A blanket delete would
    ///     destroy the staging tree of a second <c>zsup install</c> running concurrently — two
    ///     terminals, or an editor triggering one while the user runs another — and could take out
    ///     the only remaining copy of the toolchain that install had moved aside.
    /// </remarks>
    private static void SweepTransients(string downloads)
    {
        var cutoff = DateTime.UtcNow - TransientMaxAge;

        foreach (var dir in Directory.EnumerateDirectories(downloads))
        {
            var name = Path.GetFileName(dir);
            if (
                !name.StartsWith(".staging-", StringComparison.Ordinal)
                && !name.StartsWith(".trash-", StringComparison.Ordinal)
            )
                continue;

            try
            {
                if (Directory.GetCreationTimeUtc(dir) < cutoff)
                    TryDelete(dir);
            }
            catch (IOException)
            {
                // Unreadable timestamp: leave it for a later run rather than risk deleting a
                // directory that is currently in use.
            }
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort: a locked leftover is swept on a later run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
