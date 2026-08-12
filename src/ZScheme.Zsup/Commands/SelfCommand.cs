using ZScheme.Toolchain;

namespace ZScheme.Zsup.Commands;

internal static class SelfCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: zsup self <command>");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  update      Update zsup and re-stamp the shims");
            Console.WriteLine("  uninstall   Remove ~/.zscheme entirely");
            return 0;
        }

        return args[0] switch
        {
            "update" => RunUpdate(args[1..]),
            "uninstall" => RunUninstall(args[1..]),
            "--help" or "-h" => Run([]),
            _ => ZsupHelpers.Error($"error: unknown self command: {args[0]}"),
        };
    }

    private static int RunUpdate(string[] args)
    {
        string? version = null;

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--help" or "-h":
                    Console.WriteLine("Usage: zsup self update [<version>]");
                    return 0;
                default:
                    if (args[i].StartsWith('-'))
                        return ZsupHelpers.Error($"error: unknown option: {args[i]}");
                    version = args[i];
                    break;
            }

        try
        {
            return UpdateAsync(version).GetAwaiter().GetResult();
        }
        catch (Exception e)
            when (e
                    is IOException
                        or InvalidDataException
                        or InvalidOperationException
                        or FileNotFoundException
                        or ArgumentException
                        // ArchiveExtractor.Extract throws this for an unrecognized extension.
                        or NotSupportedException
                        or UnauthorizedAccessException
                        or HttpRequestException
                        or PlatformNotSupportedException
                        or TaskCanceledException
            )
        {
            return ZsupHelpers.Error($"error: {e.Message}");
        }
    }

    private static async Task<int> UpdateAsync(string? requestedVersion)
    {
        var home = ZSchemeHome.GetHome();
        var rid = RuntimeIdentifier.Detect();
        using var client = new GitHubReleaseClient();

        var release = requestedVersion is null
            ? await client.GetLatestReleaseAsync()
            : ReleaseRef.Explicit(requestedVersion);

        // Validated before it reaches an asset name and then a path under downloads/.
        ToolchainName.Validate(release.Version, nameof(requestedVersion));

        if (requestedVersion is null && release.Version == ZsupVersion.Base)
        {
            Console.WriteLine($"zsup {ZsupVersion.Base} is already the latest release");
            return 0;
        }

        var assetName = GitHubReleaseClient.ZsupAssetName(release.Version, rid);
        var downloads = ZSchemeHome.GetDownloadsDir(home);
        var archivePath = Path.Combine(downloads, assetName);

        var expected = Checksums.Find(
            await client.GetTextAssetAsync(release, Checksums.FileName),
            assetName
        );
        if (expected is null)
            throw new InvalidDataException(
                $"{Checksums.FileName} for {release.Version} does not list {assetName}"
            );

        Console.WriteLine($"downloading {assetName}");
        var actual = await client.DownloadAssetAsync(release, assetName, archivePath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(archivePath);
            throw new InvalidDataException(
                $"checksum mismatch for {assetName}"
                    + $"{Environment.NewLine}  expected {expected}"
                    + $"{Environment.NewLine}    actual {actual}"
            );
        }

        var staging = Path.Combine(downloads, ".zsup-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ArchiveExtractor.Extract(archivePath, staging);

            var newBinary = Path.Combine(staging, ZSchemeHome.ExeName("zsup"));
            if (!File.Exists(newBinary))
                throw new InvalidDataException($"{assetName} does not contain a zsup binary");

            ZsupSelf.ReplaceInstalledBinaries(newBinary, home);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
                File.Delete(archivePath);
            }
            catch (IOException)
            {
                // Swept by a later run.
            }
        }

        Console.WriteLine($"updated zsup to {release.Version}");
        return 0;
    }

    private static int RunUninstall(string[] args)
    {
        var assumeYes = false;

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--yes" or "-y":
                    assumeYes = true;
                    break;
                default:
                    return ZsupHelpers.Error($"error: unknown option: {args[i]}");
            }

        var home = ZSchemeHome.GetHome();
        if (!Directory.Exists(home))
        {
            Console.WriteLine($"nothing to remove: {home} does not exist");
            return 0;
        }

        if (!assumeYes)
        {
            Console.Error.WriteLine(
                $"error: this will delete {home} and every installed toolchain"
            );
            Console.Error.WriteLine("help: re-run with `--yes` to confirm");
            return 1;
        }

        // The running binary lives inside the tree being deleted. On Windows that file cannot be
        // removed while it is executing, so leave the removal to the user rather than half-deleting
        // their home directory.
        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine($"error: zsup cannot remove itself while running on Windows");
            Console.Error.WriteLine($"help: close this shell, then delete {home}");
            return 1;
        }

        Directory.Delete(home, recursive: true);
        Console.WriteLine($"removed {home}");
        Console.WriteLine("note: remove the ~/.zscheme/env line from your shell profile to finish");
        return 0;
    }
}
