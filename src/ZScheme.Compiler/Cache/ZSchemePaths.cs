using ZScheme.Toolchain;

namespace ZScheme.Compiler.Cache;

public static class ZSchemePaths
{
    private static readonly object Lock = new();
    private static string? _processDefaultCacheRoot;

    /// <summary>
    ///     Sets a process-wide default for the ZScheme cache root, used as a fallback when no
    ///     explicit override is passed to <see cref="GetCacheRoot" />. Intended to be called once
    ///     at startup (e.g. by the CLI reading <c>ZSCHEME_CACHE_DIR</c>). Pass <c>null</c> or an
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
    ///     > process default (see <see cref="SetProcessDefaultCacheRoot" />) > <c>&lt;home&gt;/cache</c>,
    ///     where the home is <c>ZSCHEME_HOME</c> or <c>~/.zscheme</c>. NuGet caches do not pass
    ///     through here; they stay at <c>&lt;home&gt;/cache/nuget</c> regardless of
    ///     <c>ZSCHEME_CACHE_DIR</c>.
    /// </summary>
    public static string GetCacheRoot(string? explicitOverride = null)
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

        return ZSchemeHome.GetCacheRoot();
    }

    public static string GetPackageCacheRoot(string? explicitOverride = null)
    {
        return Path.Combine(GetCacheRoot(explicitOverride), "pkg", CompilerInfo.BaseVersion);
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
