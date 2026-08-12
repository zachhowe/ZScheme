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
    ///     The compiler reads <c>cache/pkg/&lt;compiler version&gt;/</c>, which is unrelated to the
    ///     name the toolchain was installed under — <c>zsup install dev --from …</c> is legal, and
    ///     CI installs the same archive twice under different names. The shipped layout is therefore
    ///     <c>pkgcache/&lt;compiler version&gt;/&lt;package&gt;/&lt;package version&gt;/</c>, so the
    ///     version travels with the payload instead of being guessed from the install name.
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
                var dest = Path.Combine(
                    destRoot,
                    Path.GetFileName(packageDir),
                    Path.GetFileName(versionDir)
                );

                // An existing entry is left alone: it may have been rebuilt from source and is at
                // least as current as what shipped.
                if (!force && Directory.Exists(dest))
                    continue;

                ArchiveExtractor.CopyDirectory(versionDir, dest);
                seeded++;
            }
        }

        return seeded;
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

        return Directory
            .EnumerateDirectories(source)
            .Select(Path.GetFileName)
            .FirstOrDefault(name => name is not null && ToolchainName.IsValid(name));
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
            catch (Exception e) when (e is System.Text.Json.JsonException or IOException)
            {
                // Fall through to the payload.
            }
        }

        return FindCompilerVersion(toolchainDir) ?? installedAs;
    }
}
