using ZScheme.Toolchain;

namespace ZScheme.Compiler.Cache;

public static class ZSchemePaths
{
    private static readonly object Lock = new();
    private static string? _processDefaultCacheRoot;

    /// <summary>
    ///     Sets a process-wide default for the ZScheme cache root, used as a fallback when no
    ///     explicit override is passed to <see cref="GetCacheRoot" /> and ranked above
    ///     <c>ZSCHEME_CACHE_DIR</c>. Intended to be called once at startup by a host that has its
    ///     own idea of where the cache belongs; reading the environment variable is not a reason to
    ///     call it, since <see cref="GetCacheRoot" /> already does that. Pass <c>null</c> or an
    ///     empty/whitespace string to clear it.
    /// </summary>
    public static void SetProcessDefaultCacheRoot(string? value)
    {
        var normalized = Normalize(value);
        lock (Lock)
        {
            _processDefaultCacheRoot = normalized;
        }
    }

    /// <summary>
    ///     Resolves the base ZScheme cache directory. Priority: <paramref name="explicitOverride" />
    ///     > process default (see <see cref="SetProcessDefaultCacheRoot" />) >
    ///     <c>ZSCHEME_CACHE_DIR</c> > <c>&lt;home&gt;/cache</c>, where the home is
    ///     <c>ZSCHEME_HOME</c> or <c>~/.zscheme</c>. NuGet caches do not pass through here; they
    ///     stay at <c>&lt;home&gt;/cache/nuget</c> regardless of <c>ZSCHEME_CACHE_DIR</c>.
    /// </summary>
    /// <remarks>
    ///     The environment variable is read here rather than injected by each entry point. It used
    ///     to be the CLI's job, which left every other host — the language server, the macro
    ///     debugger, the fuzzer — resolving a different cache root than the one <c>zsup</c> seeds
    ///     and the one <c>zs</c> compiles into. Resolving it at the bottom is what makes that
    ///     impossible to forget.
    /// </remarks>
    public static string GetCacheRoot(string? explicitOverride = null)
    {
        return GetCacheRoot(
            explicitOverride,
            Environment.GetEnvironmentVariable(ZSchemeHome.CacheDirEnvironmentVariable)
        );
    }

    /// <summary>
    ///     Testable overload taking the environment value explicitly, so no test ever has to write
    ///     to the process environment.
    /// </summary>
    internal static string GetCacheRoot(string? explicitOverride, string? cacheDirEnvValue)
    {
        var normalizedExplicit = Normalize(explicitOverride);
        if (normalizedExplicit is not null)
            return normalizedExplicit;

        string? processDefault;
        lock (Lock)
        {
            processDefault = _processDefaultCacheRoot;
        }

        if (processDefault is not null)
            return processDefault;

        return ZSchemeHome.GetEffectiveCacheRoot(home: null, cacheDirEnvValue);
    }

    public static string GetPackageCacheRoot(string? explicitOverride = null)
    {
        return Path.Combine(GetCacheRoot(explicitOverride), "pkg", CompilerInfo.BaseVersion);
    }

    /// <inheritdoc cref="GetCacheRoot(string?, string?)" />
    internal static string GetPackageCacheRoot(string? explicitOverride, string? cacheDirEnvValue)
    {
        return Path.Combine(
            GetCacheRoot(explicitOverride, cacheDirEnvValue),
            "pkg",
            CompilerInfo.BaseVersion
        );
    }

    public static string GetGitCacheRoot(string? explicitOverride = null)
    {
        return Path.Combine(GetCacheRoot(explicitOverride), "git");
    }

    private static string? Normalize(string? value)
    {
        return PathNormalizer.Normalize(value);
    }
}
