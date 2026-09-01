namespace ZScheme.Toolchain;

/// <summary>
///     Copies the prebuilt package cache shipped inside a toolchain into the shared cache.
/// </summary>
/// <remarks>
///     Without this, the first compile against a freshly installed toolchain would have to build
///     the standard library from source — which needs a NuGet restore and the .NET SDK, so it would
///     be slow, online-only, and prone to failing. The shipped cache is small (well under a
///     megabyte for every package) and contains no absolute paths, so it can simply be copied.
///     zsup deliberately never shells out to <c>zs install</c> here, which would reintroduce the
///     dependency this exists to remove.
/// </remarks>
public static class PackageCacheSeeder
{
    /// <summary>Name of the prebuilt cache directory inside a toolchain.</summary>
    public const string DirectoryName = "pkgcache";

    /// <summary>
    ///     Seeds the shared package cache from <c>&lt;toolchainDir&gt;/pkgcache/</c>.
    /// </summary>
    /// <remarks>
    ///     The compiler reads <c>&lt;cache root&gt;/pkg/&lt;compiler version&gt;/</c>, which is
    ///     unrelated to the name the toolchain was installed under — <c>zsup install dev --from …</c>
    ///     is legal, and CI installs the same archive twice under different names. The shipped layout
    ///     is therefore <c>pkgcache/&lt;compiler version&gt;/&lt;package&gt;/&lt;package version&gt;/</c>,
    ///     so the version travels with the payload instead of being guessed from the install name.
    ///     The destination goes through <see cref="ZSchemeHome.GetPackageCacheRootFor" /> rather than
    ///     being built from the home, because <c>ZSCHEME_CACHE_DIR</c> moves the cache out from under
    ///     it — seeding the home's copy for a user who has that set would leave the first compile
    ///     building the standard library from source anyway.
    /// </remarks>
    /// <param name="force">Overwrite package versions that are already cached.</param>
    /// <returns>The number of package versions copied.</returns>
    public static int Seed(string toolchainDir, string? home = null, bool force = false)
    {
        var source = Path.Combine(toolchainDir, DirectoryName);
        if (!Directory.Exists(source))
            return 0;

        var seeded = 0;

        foreach (var compilerVersionDir in Directory.EnumerateDirectories(source))
        {
            var compilerVersion = Path.GetFileName(compilerVersionDir);
            if (!ToolchainName.IsValid(compilerVersion))
                continue;

            var destRoot = ZSchemeHome.GetPackageCacheRootFor(compilerVersion, home);

            // Layout below the version mirrors the shared cache: <package>/<package version>/.
            foreach (var packageDir in Directory.EnumerateDirectories(compilerVersionDir))
            foreach (var versionDir in Directory.EnumerateDirectories(packageDir))
            {
                // Scratch directories -- this seeder's own staging, and the compiler's when a
                // store is killed mid-flight -- are dot-prefixed, never package versions.
                if (Path.GetFileName(versionDir).StartsWith('.'))
                    continue;

                var dest = Path.Combine(
                    destRoot,
                    Path.GetFileName(packageDir),
                    Path.GetFileName(versionDir)
                );

                // An existing entry is left alone: it may have been rebuilt from source and is at
                // least as current as what shipped.
                if (!force && Directory.Exists(dest))
                    continue;

                CopyThenCommit(versionDir, dest);
                seeded++;
            }
        }

        return seeded;
    }

    /// <summary>Prefix of the private directory one cache entry is assembled in.</summary>
    private const string StagingPrefix = ".seed-";

    /// <summary>
    ///     Copies one package version into place through a staging directory, so the entry only
    ///     ever becomes visible whole.
    /// </summary>
    /// <remarks>
    ///     The presence of <paramref name="dest" /> is the whole of what a later seed reads as
    ///     "already cached" — there is no marker file and no manifest. Copying straight into it
    ///     means a seed interrupted part-way (Ctrl-C, a full disk, a killed install) leaves a
    ///     directory that exists and is missing files, and every subsequent non-force seed then
    ///     skips it, so nothing ever repairs it: the first compile fails on a package that looks
    ///     cached, and reinstalling the toolchain does not help. Assembling under a private name and
    ///     renaming makes the directory appear only once it is complete. Beside the destination
    ///     rather than in the system temp directory, because the commit is a rename and that
    ///     requires one volume — <c>ZSCHEME_CACHE_DIR</c> can put the cache anywhere.
    /// </remarks>
    private static void CopyThenCommit(string source, string dest)
    {
        var parent = Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(parent);
        SweepStaleStaging(parent);

        var staging = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            ArchiveExtractor.CopyDirectory(source, staging);

            // Only under --force, which is the one caller that reaches here with the entry already
            // present. Directory.Move has no overwrite overload, so the previous entry is moved out
            // of the way first and deleted once the new one is committed -- a delete-then-move would
            // reintroduce, for the width of a copy, the very half-populated entry this exists to
            // rule out.
            string? previous = null;
            if (Directory.Exists(dest))
            {
                previous = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N")[..12]);
                Directory.Move(dest, previous);

                // A rename carries the directory's original timestamps, so the entry cached last
                // month arrives here already older than the sweep's cutoff -- and a concurrent seed
                // would take it while it is still the only copy of what this one moved aside.
                try
                {
                    Directory.SetLastWriteTimeUtc(previous, DateTime.UtcNow);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A timestamp is not worth failing a seed over.
                }
            }

            try
            {
                Directory.Move(staging, dest);
            }
            catch
            {
                if (previous is not null && !Directory.Exists(dest))
                    TryMoveBack(previous, dest);
                throw;
            }

            if (previous is not null)
                TryDelete(previous);
        }
        finally
        {
            // A no-op once the rename consumed it.
            TryDelete(staging);
        }
    }

    private static void TryMoveBack(string previous, string dest)
    {
        try
        {
            Directory.Move(previous, dest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Reported through the original exception on its way out: that one says why the seed
            // failed, and it is the only thing that would explain a cache entry that is now gone.
            // The copy is left under its staging name, so the payload is still recoverable.
        }
    }

    /// <summary>
    ///     Deletes staging directories older than <see cref="ZSchemeHome.StagingMaxAge" /> beside
    ///     <paramref name="parent" />.
    /// </summary>
    /// <remarks>
    ///     The <c>finally</c> around the rename covers a failure but not a kill during the copy, and
    ///     nothing else walks the cache looking for these. Age-gated for the reason every other
    ///     staging sweep here is: a concurrent install seeding the same package has one of its own,
    ///     and deleting it leaves that process renaming a path that no longer exists. They are
    ///     invisible to the compiler either way — it looks a package version up by exact path, and
    ///     the leading '.' is not a version any manifest names.
    /// </remarks>
    private static void SweepStaleStaging(string parent)
    {
        var cutoff = DateTime.UtcNow - ZSchemeHome.StagingMaxAge;

        try
        {
            foreach (var stale in Directory.EnumerateDirectories(parent, StagingPrefix + "*"))
                if (Directory.GetLastWriteTimeUtc(stale) < cutoff)
                    TryDelete(stale);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a stale directory costs disk, not correctness, and must never be the
            // reason a package was left unseeded.
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Swept by a later seed.
        }
    }

    /// <summary>
    ///     The compiler version a toolchain's shipped cache is for, read from the payload itself, or
    ///     <c>null</c> when it ships none.
    /// </summary>
    public static string? FindCompilerVersion(string toolchainDir)
    {
        var source = Path.Combine(toolchainDir, DirectoryName);
        if (!Directory.Exists(source))
            return null;

        try
        {
            return Directory
                .EnumerateDirectories(source)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => name is not null && ToolchainName.IsValid(name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Guarded for the same reason ResolveCompilerVersion guards the metadata read it tries
            // first: `zsup uninstall` calls that with no handler of its own, and an unreadable
            // payload -- the root-owned case -- must not kill the command before it has removed
            // anything. Reporting no shipped cache is the same answer as shipping none.
            return null;
        }
    }

    /// <summary>
    ///     The compiler version an installed toolchain reports, preferring the recorded
    ///     <c>toolchain.json</c> and falling back to the shipped cache or the install name. This is
    ///     the key into <c>cache/pkg/</c> for that toolchain.
    /// </summary>
    public static string ResolveCompilerVersion(string toolchainDir, string installedAs)
    {
        var metadataPath = Path.Combine(toolchainDir, "toolchain.json");
        if (File.Exists(metadataPath))
        {
            try
            {
                var metadata = System.Text.Json.JsonSerializer.Deserialize(
                    File.ReadAllText(metadataPath),
                    ToolchainJsonContext.Default.ToolchainMetadata
                );

                if (
                    metadata?.Version is { Length: > 0 } recorded
                    && ToolchainName.IsValid(recorded)
                )
                    return recorded;
            }
            catch (Exception e)
                when (e
                        is System.Text.Json.JsonException
                            or IOException
                            or UnauthorizedAccessException
                )
            {
                // Fall through to the payload. UnauthorizedAccessException is not an IOException
                // and is what a toolchain.json under a root-owned toolchain directory raises;
                // `zsup uninstall` calls this with no handler of its own, so throwing here would
                // kill the command before it reported anything rather than costing it the recorded
                // version it can recover from the payload anyway.
            }
        }

        return FindCompilerVersion(toolchainDir) ?? installedAs;
    }
}
