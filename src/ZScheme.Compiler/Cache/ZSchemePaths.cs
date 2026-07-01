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
    ///     > process default (see <see cref="SetProcessDefaultCacheRoot" />) >
    ///     <c>~/.zscheme/cache</c>. NuGet caches do not pass through here; they remain at
    ///     <c>~/.zscheme/cache/nuget</c> regardless.
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

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zscheme",
            "cache"
        );
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
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var expanded = Environment.ExpandEnvironmentVariables(trimmed);

        if (expanded.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (expanded.Length == 1)
                expanded = home;
            else if (expanded[1] == Path.DirectorySeparatorChar || expanded[1] == '/')
                expanded = Path.Combine(home, expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }
}
