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

    /// <summary>Name of the per-directory toolchain pin file.</summary>
    public const string VersionFileName = ".zscheme-version";

    /// <summary>Suffix marking a linked (developer) toolchain under <c>toolchains/</c>.</summary>
    public const string LinkFileExtension = ".link";

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
    ///     The default cache root. This answers the *layout* question only — the compiler's
    ///     <c>ZSchemePaths.GetCacheRoot</c> still applies its own overrides on top, so
    ///     <c>ZSCHEME_CACHE_DIR</c> continues to win over <c>ZSCHEME_HOME</c>.
    /// </summary>
    public static string GetCacheRoot(string? home = null)
    {
        return Path.Combine(GetHome(home), "cache");
    }

    /// <summary>
    ///     Package cache for a specific compiler version. Takes the version explicitly because this
    ///     assembly must not reference the compiler (which is where <c>CompilerInfo</c> lives).
    /// </summary>
    public static string GetPackageCacheRootFor(string version, string? home = null)
    {
        return Path.Combine(GetCacheRoot(home), "pkg", version);
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
