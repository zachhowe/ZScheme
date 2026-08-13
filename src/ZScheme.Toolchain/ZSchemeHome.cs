namespace ZScheme.Toolchain;

/// <summary>
///     The single definition of the ZScheme home layout. Both the compiler (through
///     <c>ZSchemePaths</c>) and the <c>zsup</c> toolchain manager resolve paths through here, so the
///     two can never disagree about where things live.
/// </summary>
/// <remarks>
///     Every member takes an optional explicit home so callers — and especially tests — can point at
///     a scratch directory without touching environment variables. See the note on
///     <c>FrameworkResolver.Resolve</c> for why mutating the environment in tests is avoided.
/// </remarks>
public static class ZSchemeHome
{
    public const string HomeEnvironmentVariable = "ZSCHEME_HOME";
    public const string VersionEnvironmentVariable = "ZSCHEME_VERSION";
    public const string CacheDirEnvironmentVariable = "ZSCHEME_CACHE_DIR";

    /// <summary>Name of the per-directory toolchain pin file.</summary>
    public const string VersionFileName = ".zscheme-version";

    /// <summary>Suffix marking a linked (developer) toolchain under <c>toolchains/</c>.</summary>
    public const string LinkFileExtension = ".link";

    /// <summary>
    ///     How old a leftover staging slot must be before a sweep treats it as debris and deletes
    ///     it.
    /// </summary>
    /// <remarks>
    ///     Every write in the home that has to be atomic stages under a per-process name and renames
    ///     it into place, and every one of those sweeps the slots a killed run left behind. The gate
    ///     is what keeps a sweep off the slots that are still live: the point of naming a slot
    ///     per-process is that a concurrent zsup has one of its own, and deleting that one leaves it
    ///     renaming a path that no longer exists. A live slot is written and renamed in
    ///     milliseconds, so an hour is far outside any real one while still bounding what an
    ///     interrupted run can accumulate. Shared rather than restated at each sweep so the sites
    ///     cannot drift into disagreeing about what counts as abandoned.
    /// </remarks>
    public static readonly TimeSpan StagingMaxAge = TimeSpan.FromHours(1);

    /// <summary>
    ///     Resolves the ZScheme home. Priority: <paramref name="explicitOverride" /> &gt;
    ///     <c>ZSCHEME_HOME</c> &gt; <c>~/.zscheme</c>.
    /// </summary>
    public static string GetHome(string? explicitOverride = null)
    {
        return GetHome(
            explicitOverride,
            Environment.GetEnvironmentVariable(HomeEnvironmentVariable)
        );
    }

    /// <summary>
    ///     Testable overload taking the environment value explicitly, so no test ever has to write
    ///     to the process environment.
    /// </summary>
    internal static string GetHome(string? explicitOverride, string? envValue)
    {
        return PathNormalizer.Normalize(explicitOverride)
            ?? PathNormalizer.Normalize(envValue)
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".zscheme"
            );
    }

    /// <summary>The directory the user puts on PATH; holds <c>zsup</c> and the shims.</summary>
    public static string GetBinDir(string? home = null)
    {
        return Path.Combine(GetHome(home), "bin");
    }

    /// <summary>True when <paramref name="dir" /> is this home's own <see cref="GetBinDir" />.</summary>
    /// <remarks>
    ///     A toolchain whose binaries resolve to this directory is a trap rather than a toolchain:
    ///     the <c>zs</c> here is the zsup shim, so handing off to it re-enters the shim, which
    ///     resolves the same toolchain and hands off again. <c>zsup link dev ~/.zscheme/bin</c> --
    ///     or linking the home itself, whose <c>bin/</c> is found the same way -- is all it takes to
    ///     reach it, so both the link and the handoff check.
    /// </remarks>
    public static bool IsBinDir(string dir, string? home = null)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(GetBinDir(home))),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            );
        }
        catch (Exception e)
            when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // A path the OS will not even parse is not this directory. Answering rather than
            // throwing matters here: every `zs` invocation asks this question, and the caller's
            // own "no zs there" path already covers a toolchain pointing somewhere unusable.
            return false;
        }
    }

    public static string GetToolchainsDir(string? home = null)
    {
        return Path.Combine(GetHome(home), "toolchains");
    }

    /// <summary>Directory of an installed toolchain. Rejects names that could escape the root.</summary>
    public static string GetToolchainDir(string name, string? home = null)
    {
        return Path.Combine(GetToolchainsDir(home), ToolchainName.Validate(name));
    }

    /// <summary>The one-line pointer file registered by <c>zsup link</c>.</summary>
    public static string GetToolchainLinkFile(string name, string? home = null)
    {
        return Path.Combine(
            GetToolchainsDir(home),
            ToolchainName.Validate(name) + LinkFileExtension
        );
    }

    public static string GetSettingsFile(string? home = null)
    {
        return Path.Combine(GetHome(home), "settings.json");
    }

    /// <summary>
    ///     Scratch space for downloads and for staging an install. Deliberately under the home
    ///     rather than the system temp dir: installs commit with a directory rename into
    ///     <c>toolchains/</c>, which requires both to be on the same volume.
    /// </summary>
    public static string GetDownloadsDir(string? home = null)
    {
        return Path.Combine(GetHome(home), "downloads");
    }

    public static string GetEnvFile(string? home = null)
    {
        return Path.Combine(GetHome(home), "env");
    }

    public static string GetEnvFishFile(string? home = null)
    {
        return Path.Combine(GetHome(home), "env.fish");
    }

    /// <summary>
    ///     The cache root a home lays out by default. This answers the *layout* question only; it is
    ///     not where the caches subject to <c>ZSCHEME_CACHE_DIR</c> actually live — see
    ///     <see cref="GetEffectiveCacheRoot" />. The NuGet cache is the one that stays here
    ///     unconditionally.
    /// </summary>
    public static string GetCacheRoot(string? home = null)
    {
        return Path.Combine(GetHome(home), "cache");
    }

    /// <summary>
    ///     The cache root a compile will actually read: <c>ZSCHEME_CACHE_DIR</c> when it is set,
    ///     otherwise <see cref="GetCacheRoot" />.
    /// </summary>
    /// <remarks>
    ///     The environment variable wins even over an explicitly passed <paramref name="home" />,
    ///     which is the opposite of how <see cref="GetHome" /> treats <c>ZSCHEME_HOME</c> — and it
    ///     has to be. zsup always resolves the home first and then passes it explicitly, so
    ///     deferring to it here would mean seeding a directory no compile ever looks at for every
    ///     user who has <c>ZSCHEME_CACHE_DIR</c> exported. This mirrors the compiler's
    ///     <c>ZSchemePaths.GetCacheRoot</c>, which the CLI feeds from the same variable.
    /// </remarks>
    public static string GetEffectiveCacheRoot(string? home = null)
    {
        return GetEffectiveCacheRoot(
            home,
            Environment.GetEnvironmentVariable(CacheDirEnvironmentVariable)
        );
    }

    /// <summary>
    ///     Overload taking the environment value explicitly, for callers that have already read it —
    ///     and so no test ever has to write to the process environment.
    /// </summary>
    public static string GetEffectiveCacheRoot(string? home, string? cacheDirEnvValue)
    {
        return PathNormalizer.Normalize(cacheDirEnvValue) ?? GetCacheRoot(home);
    }

    /// <summary>
    ///     Package cache for a specific compiler version. Takes the version explicitly because this
    ///     assembly must not reference the compiler (which is where <c>CompilerInfo</c> lives).
    /// </summary>
    public static string GetPackageCacheRootFor(string version, string? home = null)
    {
        return Path.Combine(GetEffectiveCacheRoot(home), "pkg", version);
    }

    /// <inheritdoc cref="GetEffectiveCacheRoot(string?, string?)" />
    public static string GetPackageCacheRootFor(
        string version,
        string? home,
        string? cacheDirEnvValue
    )
    {
        return Path.Combine(GetEffectiveCacheRoot(home, cacheDirEnvValue), "pkg", version);
    }

    /// <summary>
    ///     Isolated cache root for a linked developer toolchain. A linked build reports the same
    ///     compiler version as the released one, so without this it would share — and could poison —
    ///     the released toolchain's compiled packages.
    /// </summary>
    public static string GetLinkedCacheRoot(string name, string? home = null)
    {
        return Path.Combine(GetHome(home), "cache-dev", ToolchainName.Validate(name));
    }

    public static string GetNuGetCacheRoot(string? home = null)
    {
        return Path.Combine(GetCacheRoot(home), "nuget");
    }

    /// <summary>Appends <c>.exe</c> on Windows.</summary>
    public static string ExeName(string baseName)
    {
        return OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
    }
}
