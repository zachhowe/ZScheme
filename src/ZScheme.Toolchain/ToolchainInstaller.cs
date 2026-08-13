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

    /// <summary>
    ///     Prefixes of the directories <c>downloads/</c> is staged under: an install's extraction
    ///     tree, the installation it moved aside, <c>zsup self update</c>'s own staging tree, and
    ///     the slot a download streams into.
    /// </summary>
    private static readonly string[] TransientDirPrefixes =
    [
        ".staging-",
        ".trash-",
        ".zsup-",
        DownloadSlotPrefix,
    ];

    /// <summary>Prefix of the per-download directory <see cref="CreateDownloadSlot" /> hands out.</summary>
    private const string DownloadSlotPrefix = ".dl-";

    /// <summary>What the two release assets zsup downloads are named after.</summary>
    private static readonly string[] ReleaseAssetPrefixes = ["zscheme-", "zsup-"];

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

        if (!force && ExplainNameTaken(name) is { } taken)
            throw new IOException(taken);

        var destDir = ZSchemeHome.GetToolchainDir(name, _home);
        var linkFile = ZSchemeHome.GetToolchainLinkFile(name, _home);

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
            RequireShims(staging);
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
    ///     Why installing under <paramref name="name" /> would be refused without <c>--force</c>, or
    ///     <c>null</c> when the name is free.
    /// </summary>
    /// <remarks>
    ///     <see cref="InstallFrom" /> is the enforcement point and asks this first — <c>--from</c>,
    ///     the installer scripts and any future caller reach it directly, so nothing may bypass it.
    ///     It is public because the release path reaches <see cref="InstallFrom" /> only with the
    ///     archive already on disk, and the answer here needs no archive at all: without an earlier
    ///     look, `zsup install latest` pays a full download and a SHA-256 over it to learn what a
    ///     <see cref="Directory.Exists" /> would have said. One method rather than a second pair of
    ///     Exists calls, so the early check and the guard cannot drift apart about what counts as
    ///     installed.
    /// </remarks>
    public string? ExplainNameTaken(string name)
    {
        if (Directory.Exists(ZSchemeHome.GetToolchainDir(name, _home)))
            return $"toolchain '{name}' is already installed; pass --force to replace it";

        // The reciprocal of the guard in ToolchainRegistry.Link. Without it a name can end up with
        // both a directory and a .link file, where the directory shadows the link in List and
        // TryGet -- so nothing but `zsup unlink <name>` would ever mention the link again. List and
        // Remove both cope with the collision; this keeps it from being made in the first place.
        if (File.Exists(ZSchemeHome.GetToolchainLinkFile(name, _home)))
            return $"'{name}' is a linked toolchain; run `zsup unlink {name}` first, or pass --force to replace it";

        return null;
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
    ///     Rejects a payload that is missing any of the executables a toolchain is made of.
    /// </summary>
    /// <remarks>
    ///     <c>zs</c> and <c>zs-lsp</c> ship together — <c>publish.ps1</c> publishes both projects
    ///     into one <c>bin/</c>, and <see cref="ShimInstaller" /> stamps both names in
    ///     <c>~/.zscheme/bin</c> whatever the selected toolchain turns out to hold. A payload
    ///     carrying only one of them installs cleanly and lists as a working toolchain, so the gap
    ///     surfaces much later as <c>toolchain 'x' has no zs-lsp</c> from inside an editor, where
    ///     nothing connects it to the install that caused it. <c>--from</c> a per-project build
    ///     output is how it happens in practice: the CLI and the language server are separate
    ///     projects with separate output directories.
    ///     <para>
    ///         Checked after <see cref="NormalizeLayout" /> so it reads the settled <c>bin/</c>
    ///         rather than having to know which of the two input layouts it was given, and before
    ///         <see cref="MarkExecutables" /> because a payload being rejected is not worth chmod'ing.
    ///     </para>
    /// </remarks>
    private static void RequireShims(string staging)
    {
        var binDir = Path.Combine(staging, "bin");
        var missing = ShimInstaller
            .ShimNames.Select(ZSchemeHome.ExeName)
            .Where(exe => !File.Exists(Path.Combine(binDir, exe)))
            .ToArray();

        if (missing.Length == 0)
            return;

        throw new InvalidOperationException(
            $"the archive does not contain {string.Join(" or ", missing)}; "
                + "zs and zs-lsp ship together"
        );
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
    ///     Creates a directory under <paramref name="downloads" /> for one download to land in.
    /// </summary>
    /// <remarks>
    ///     An archive is written under a slot of its own rather than to
    ///     <c>downloads/&lt;asset name&gt;</c>, because that path is a function of the version
    ///     alone: two zsup processes fetching one version — a CI job and an editor, or two
    ///     terminals — otherwise write the same file, and whichever finishes first deletes the
    ///     archive the other is still extracting. Inside the slot the asset keeps its published
    ///     name, which is what tells <see cref="ArchiveExtractor" /> whether it has a zip or a
    ///     tarball.
    ///     <para>
    ///         Swept by <see cref="SweepTransients" /> along with every other transient here, so a
    ///         run killed mid-download does not leave a toolchain-sized file behind for good.
    ///     </para>
    /// </remarks>
    public static string CreateDownloadSlot(string downloads)
    {
        var slot = Path.Combine(downloads, DownloadSlotPrefix + Guid.NewGuid().ToString("N")[..12]);

        Directory.CreateDirectory(slot);
        return slot;
    }

    /// <summary>
    ///     Removes the debris of an interrupted install or self-update: staging, trash and download
    ///     directories, partial downloads, and downloaded release archives.
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
    ///     <para>
    ///         Public because <c>zsup self update</c> stages in this same directory and never
    ///         reaches <see cref="InstallFrom" />. Both paths delete their own archive and staging
    ///         tree when they finish; this is what covers the times they cannot — a scanner holding
    ///         a freshly written file, or a kill before the cleanup runs at all — which without a
    ///         sweep leaves hundreds of megabytes in <c>downloads/</c> permanently.
    ///     </para>
    /// </remarks>
    public static void SweepTransients(string downloads)
    {
        if (!Directory.Exists(downloads))
            return;

        var cutoff = DateTime.UtcNow - TransientMaxAge;

        foreach (var dir in Directory.EnumerateDirectories(downloads))
        {
            var name = Path.GetFileName(dir);
            if (
                !Array.Exists(
                    TransientDirPrefixes,
                    p => name.StartsWith(p, StringComparison.Ordinal)
                )
            )
                continue;

            if (IsOlderThan(dir, cutoff, Directory.GetLastWriteTimeUtc))
                TryDelete(dir);
        }

        // A download that was interrupted or failed verification leaves a .part behind, and one
        // that completed leaves the archive itself whenever the delete that follows it could not
        // run. Neither is ever reused -- the next attempt starts a fresh .part and re-hashes what
        // it wrote -- so nothing else would ever remove them.
        foreach (var file in Directory.EnumerateFiles(downloads))
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".part", StringComparison.Ordinal) && !IsReleaseArchive(name))
                continue;

            if (IsOlderThan(file, cutoff, File.GetLastWriteTimeUtc))
                TryDeleteFile(file);
        }
    }

    /// <summary>
    ///     Whether a file in <c>downloads/</c> is a release asset zsup put there itself.
    /// </summary>
    /// <remarks>
    ///     Matched on the exact shape <see cref="GitHubReleaseClient.ToolchainAssetName" /> and
    ///     <see cref="GitHubReleaseClient.ZsupAssetName" /> produce — prefix, a version, a supported
    ///     RID, and the extension that RID publishes — rather than on the prefix and extension
    ///     alone, so a file the user parked here to install with <c>--from</c> is left alone even
    ///     when they named it after the project. <c>zscheme-nightly.tar.gz</c> is nobody's release
    ///     asset, and deleting it six hours later is not a sweep of zsup's own debris.
    /// </remarks>
    private static bool IsReleaseArchive(string name)
    {
        foreach (var prefix in ReleaseAssetPrefixes)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            foreach (var rid in RuntimeIdentifier.Supported)
            {
                var suffix = $"-{rid}{RuntimeIdentifier.ArchiveExtension(rid)}";
                if (!name.EndsWith(suffix, StringComparison.Ordinal))
                    continue;

                // The prefix and the suffix can overlap, and then the range below runs backwards
                // and throws rather than yielding an empty version: `zsup-win-x64.zip` both starts
                // with `zsup-` and ends with `-win-x64.zip`, which is one character more than the
                // name is long. That is not a hypothetical shape -- it is the release asset name
                // with the version left out -- and the sweep this feeds runs before the try in
                // InstallFrom, so one such file parked in downloads/ would fail every `zsup
                // install` and `zsup self update` from then on.
                if (name.Length < prefix.Length + suffix.Length)
                    continue;

                // What is left between the two is the version the asset was published for. It is
                // validated rather than merely required to be non-empty because it is the same
                // string a toolchain is named after, and nothing else here says the file came from
                // a release rather than from the user.
                if (ToolchainName.IsValid(name[prefix.Length..^suffix.Length]))
                    return true;
            }
        }

        return false;
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
