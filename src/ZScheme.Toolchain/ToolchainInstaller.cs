using System.Text.Json;

namespace ZScheme.Toolchain;

/// <summary>Installs a toolchain into <c>~/.zscheme/toolchains/&lt;name&gt;</c>.</summary>
public sealed class ToolchainInstaller(string? home = null)
{
    /// <summary>
    ///     How old a leftover transient must be before it is swept. Long enough that a concurrently
    ///     running install is never mistaken for debris.
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

            Stamp(staging);
            NormalizeLayout(staging, source);
            MarkExecutables(staging);
            WriteMetadata(staging, name, source, sha256);

            Directory.CreateDirectory(ZSchemeHome.GetToolchainsDir(_home));

            if (Directory.Exists(destDir))
            {
                // Moved aside rather than deleted, so a failure part-way through the rename does
                // not leave the user with neither the old nor the new toolchain.
                trash = Path.Combine(downloads, ".trash-" + Guid.NewGuid().ToString("N")[..12]);
                Directory.Move(destDir, trash);

                // Not optional. A rename keeps the directory's original timestamps, so a toolchain
                // installed a month ago becomes a .trash- entry that is already a month "old" --
                // and a concurrently running install would sweep it on its very next run, which is
                // exactly the case the sweep exists to avoid.
                Stamp(trash);
            }

            // The commit point.
            Directory.Move(staging, destDir);
        }
        catch
        {
            TryDelete(staging);
            // Put the previous installation back if we had already moved it aside. Guarded, because
            // a failure here must not replace the exception on its way out: that one says why the
            // install failed -- a bad archive, a checksum, a missing zs -- and is the only thing
            // that would explain a toolchain that has now gone missing. The trash directory is left
            // where it is, so the previous payload can still be recovered from downloads/.
            if (trash is not null && Directory.Exists(trash) && !Directory.Exists(destDir))
                try
                {
                    Directory.Move(trash, destDir);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Reported through the original exception below.
                }

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
    private static void NormalizeLayout(string staging, string source)
    {
        var binDir = Path.Combine(staging, "bin");
        if (File.Exists(Path.Combine(binDir, ZSchemeHome.ExeName("zs"))))
            return;

        if (!File.Exists(Path.Combine(staging, ZSchemeHome.ExeName("zs"))))
            throw new InvalidOperationException(
                $"the archive does not contain {ZSchemeHome.ExeName("zs")}; it does not look like a ZScheme toolchain"
            );

        if (File.Exists(binDir))
            throw new InvalidOperationException(
                $"{source} has a file named 'bin' where the binaries directory belongs; it does not look like a ZScheme toolchain"
            );

        // A bin/ that exists but has no zs in it -- a dev staging tree, or a future layout change.
        // Its contents are merged rather than replaced, because moving over it would fail with a
        // bare "cannot create ... already exists" that says nothing about the real cause.
        var hadBinDir = Directory.Exists(binDir);

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
            // bin/ stays put when it already exists: it is the destination, not something to
            // relocate into itself.
            if (
                entryName == Path.GetFileName(temp)
                || keepAtRoot.Contains(entryName)
                || (hadBinDir && entryName == "bin")
            )
                continue;

            var target = Path.Combine(temp, entryName);
            if (Directory.Exists(entry))
                Directory.Move(entry, target);
            else
                File.Move(entry, target);
        }

        if (!hadBinDir)
        {
            Directory.Move(temp, binDir);
            return;
        }

        foreach (var entry in Directory.GetFileSystemEntries(temp))
            MoveInto(entry, Path.Combine(binDir, Path.GetFileName(entry)));

        Directory.Delete(temp);
    }

    /// <summary>
    ///     Moves <paramref name="source" /> to <paramref name="target" />, merging directory into
    ///     directory when the target is already there.
    /// </summary>
    /// <remarks>
    ///     <see cref="Directory.Move(string, string)" /> has no overwrite overload and throws on any
    ///     existing target, so a plain move would reintroduce the bare "cannot create ... already
    ///     exists" that the merge in <see cref="NormalizeLayout" /> exists to avoid — just for
    ///     directories rather than files. A dev staging tree with its own <c>bin/runtimes/</c>
    ///     beside a payload that also ships one is enough to hit it.
    /// </remarks>
    private static void MoveInto(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            File.Move(source, target, overwrite: true);
            return;
        }

        if (!Directory.Exists(target))
        {
            Directory.Move(source, target);
            return;
        }

        foreach (var entry in Directory.GetFileSystemEntries(source))
            MoveInto(entry, Path.Combine(target, Path.GetFileName(entry)));

        Directory.Delete(source);
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
    ///     Stamps a transient as belonging to this install, which is what <see cref="SweepTransients" />
    ///     ages it by.
    /// </summary>
    private static void Stamp(string dir)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Worst case the transient looks older than it is and a later install sweeps it; the
            // install itself has no reason to fail over a timestamp.
        }
    }

    /// <summary>
    ///     Removes the debris of an interrupted install: staging and trash directories, and partial
    ///     downloads.
    /// </summary>
    /// <remarks>
    ///     Only entries older than <see cref="TransientMaxAge" /> are swept. A blanket delete would
    ///     destroy the staging tree of a second <c>zsup install</c> running concurrently — two
    ///     terminals, or an editor triggering one while the user runs another — and could take out
    ///     the only remaining copy of the toolchain that install had moved aside. Age comes from the
    ///     write time <see cref="Stamp" /> sets, not from the creation time: a trash directory is
    ///     produced by renaming an installed toolchain, and a rename carries the original
    ///     timestamps, so an entry moved aside seconds ago would otherwise be as old as the
    ///     toolchain itself.
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

            if (IsOlderThan(dir, cutoff, Directory.GetLastWriteTimeUtc))
                TryDelete(dir);
        }

        // A download that was interrupted or failed verification leaves one of these behind. They
        // are never resumed -- the next attempt starts a fresh .part -- so nothing else would ever
        // remove them.
        foreach (var file in Directory.EnumerateFiles(downloads, "*.part"))
            if (IsOlderThan(file, cutoff, File.GetLastWriteTimeUtc))
                TryDeleteFile(file);
    }

    private static bool IsOlderThan(string path, DateTime cutoff, Func<string, DateTime> writeTime)
    {
        try
        {
            return writeTime(path) < cutoff;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable timestamp: leave it for a later run rather than risk deleting something
            // that is currently in use.
            return false;
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
