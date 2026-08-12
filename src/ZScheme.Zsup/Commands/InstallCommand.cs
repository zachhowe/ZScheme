using ZScheme.Toolchain;

namespace ZScheme.Zsup.Commands;

internal static class InstallCommand
{
    internal static int Run(string[] args)
    {
        string? spec = null;
        string? from = null;
        var force = false;
        var noDefault = false;

        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--from" when i + 1 < args.Length:
                    from = args[++i];
                    break;
                case "--force":
                    force = true;
                    break;
                case "--no-default":
                    noDefault = true;
                    break;
                case "--help" or "-h":
                    Console.WriteLine(
                        "Usage: zsup install <version|latest> [--from <archive|dir>] [--force] [--no-default]"
                    );
                    return 0;
                default:
                    if (args[i].StartsWith('-'))
                        return ZsupHelpers.Error($"error: unknown option: {args[i]}");
                    if (spec is not null)
                        return ZsupHelpers.Error($"error: unexpected argument: {args[i]}");
                    spec = args[i];
                    break;
            }

        if (spec is null)
            return ZsupHelpers.Error(
                "error: expected a version",
                "usage: zsup install <version|latest> [--from <archive|dir>]"
            );

        var home = ZSchemeHome.GetHome();

        if (from is not null && spec == "latest")
            return ZsupHelpers.Error(
                "error: `latest` has to be looked up from a release, so it cannot be combined with --from",
                "help: pass the explicit version the archive contains"
            );

        ToolchainInstaller.Result result;
        try
        {
            result = from is not null
                ? InstallLocal(home, Path.GetFullPath(from), spec, force)
                : InstallFromReleaseAsync(home, spec, force).GetAwaiter().GetResult();
        }
        catch (Exception e)
            when (e
                    is IOException
                        or InvalidDataException
                        or InvalidOperationException
                        or FileNotFoundException
                        or ArgumentException
                        or HttpRequestException
                        or PlatformNotSupportedException
                        or TaskCanceledException
            )
        {
            return ZsupHelpers.Error($"error: {e.Message}");
        }

        Console.WriteLine($"installed toolchain '{result.Name}' to {result.Dir}");
        if (result.PackagesSeeded > 0)
            Console.WriteLine($"seeded {result.PackagesSeeded} prebuilt package(s) into the cache");

        StampShims(home);

        // A freshly installed toolchain becomes the default; `--no-default` is how you install one
        // without switching to it.
        var registry = new ToolchainRegistry(home);
        if (!noDefault)
        {
            registry.SetDefault(result.Name);
            Console.WriteLine($"default toolchain is now '{result.Name}'");
        }

        ZsupDoctor.WarnIfBinDirNotOnPath(home);
        ZsupDoctor.WarnIfRuntimeMissing();
        return 0;
    }

    private static ToolchainInstaller.Result InstallLocal(
        string home,
        string source,
        string name,
        bool force
    )
    {
        ToolchainName.Validate(name);
        Console.WriteLine($"installing '{name}' from {source}");
        return new ToolchainInstaller(home).InstallFrom(source, name, force);
    }

    /// <summary>Resolves the version, downloads its archive, verifies it, and installs it.</summary>
    private static async Task<ToolchainInstaller.Result> InstallFromReleaseAsync(
        string home,
        string spec,
        bool force
    )
    {
        var rid = RuntimeIdentifier.Detect();
        using var client = new GitHubReleaseClient();

        var version = spec == "latest" ? await client.GetLatestVersionAsync() : spec;
        ToolchainName.Validate(version, nameof(spec));

        if (spec == "latest")
            Console.WriteLine($"latest release is {version}");

        var assetName = GitHubReleaseClient.ToolchainAssetName(version, rid);
        var downloads = ZSchemeHome.GetDownloadsDir(home);
        var archivePath = Path.Combine(downloads, assetName);

        // Fetched before the archive so a mismatch is caught without a second download, and so a
        // release published without checksums fails loudly rather than installing unverified.
        var expected = Checksums.Find(
            await client.GetTextAssetAsync(version, Checksums.FileName),
            assetName
        );

        if (expected is null)
            throw new InvalidDataException(
                $"{Checksums.FileName} for {version} does not list {assetName}"
            );

        Console.WriteLine($"downloading {assetName}");
        var actual = await client.DownloadAssetAsync(version, assetName, archivePath);

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(archivePath);
            throw new InvalidDataException(
                $"checksum mismatch for {assetName}"
                    + $"{Environment.NewLine}  expected {expected}"
                    + $"{Environment.NewLine}    actual {actual}"
            );
        }

        try
        {
            return new ToolchainInstaller(home).InstallFrom(archivePath, version, force, actual);
        }
        finally
        {
            // The extracted toolchain is the artifact worth keeping, not the archive.
            try
            {
                File.Delete(archivePath);
            }
            catch (IOException)
            {
                // Swept by the next install.
            }
        }
    }

    /// <summary>
    ///     Re-stamps <c>zs</c>/<c>zs-lsp</c> next to whichever zsup is running, so the shims and the
    ///     manager can never drift apart.
    /// </summary>
    private static void StampShims(string home)
    {
        var binDir = ZSchemeHome.GetBinDir(home);
        var installedZsup = Path.Combine(binDir, ZSchemeHome.ExeName("zsup"));

        // Stamped from the zsup that lives in the bin directory rather than from the running
        // process. That keeps the invariant exact -- the shims are always the same binary as the
        // zsup beside them -- and it sidesteps comparing Environment.ProcessPath (symlink-resolved
        // via /proc/self/exe on Linux) against a bin path built by string concatenation, which
        // would never match on a home reached through a symlink or automount and would silently
        // leave the user with no shims at all.
        if (!File.Exists(installedZsup))
        {
            // The normal path for a developer running a freshly built zsup against their own home.
            ZsupHelpers.Warn($"no zsup in {binDir}, so `zs` and `zs-lsp` were not created there");
            Console.Error.WriteLine("help: run the installer to set up the shims");
            return;
        }

        try
        {
            ShimInstaller.Install(binDir, installedZsup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ZsupHelpers.Warn($"could not refresh the shims in {binDir}: {e.Message}");
        }
    }
}
