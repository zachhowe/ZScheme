using System.Collections.Concurrent;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     Decides which on-disk files the workspace index is allowed to see. Without this
///     the startup scan indexes every <c>.zs</c> under the workspace — including
///     generated trees like the fuzzer's <c>fuzz-runs/</c> (thousands of
///     <c>original.zs</c> repro dumps), which both dwarfs the real source in scan cost
///     and pollutes workspace-symbol search, go-to-definition, and call hierarchy with
///     machine-generated names.
/// </summary>
/// <remarks>
///     Two layers: a built-in deny list of generated/vendor directory names (plus every
///     dot-directory), and the workspace's own <c>.gitignore</c> files — the general
///     rule, since a repo already declares what is not source. Deny-list and
///     dot-directory checks only apply to segments *below* a registered root, so a
///     workspace that legitimately lives under e.g. <c>~/.config/proj</c> does not
///     exclude itself.
/// </remarks>
internal sealed class WorkspaceExclusions
{
    private static readonly char[] SeparatorChars =
    [
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar,
    ];

    /// <summary>Directory names that never hold source, whether or not the workspace is
    ///     a git repository.</summary>
    private static readonly HashSet<string> DeniedDirectoryNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "bin",
        "obj",
        "node_modules",
        "target",
        "dist",
        "coverage",
        "TestResults",
    };

    private sealed record CachedRules(DateTime Stamp, GitIgnoreRules? Rules);

    private readonly ConcurrentDictionary<string, byte> _roots = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly ConcurrentDictionary<string, string> _scanStarts = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly ConcurrentDictionary<string, CachedRules> _gitIgnoreCache = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Registers workspace roots so later single-path checks (file watcher
    ///     events) can be resolved relative to them.</summary>
    public void AddRoots(IEnumerable<string> roots)
    {
        foreach (var root in roots)
            _roots.TryAdd(Normalize(root), 0);
    }

    public void RemoveRoot(string root)
    {
        _roots.TryRemove(Normalize(root), out _);
    }

    /// <summary>
    ///     Enumerates the indexable <c>.zs</c> files under <paramref name="root" />,
    ///     pruning excluded directories rather than descending them — the point of the
    ///     walk, since a skipped <c>fuzz-runs/</c> costs one check instead of thousands.
    ///     Pruning also gives git's rule that a negation cannot re-include a file whose
    ///     parent directory is excluded.
    /// </summary>
    public IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var start = Normalize(root);
        var stack = new Stack<string>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.zs");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
                if (!IsExcluded(file, isDirectory: false))
                    yield return Path.GetFullPath(file);

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
                if (!IsExcluded(subdirectory, isDirectory: true))
                    stack.Push(subdirectory);
        }
    }

    /// <summary>Whether <paramref name="fullPath" /> is excluded from indexing, either
    ///     itself or through an excluded ancestor directory.</summary>
    public bool IsExcluded(string fullPath, bool isDirectory)
    {
        string full;
        try
        {
            full = Normalize(fullPath);
        }
        catch
        {
            return true;
        }

        var root = FindRoot(full);
        if (root is null)
            return IsGeneratedOutputPath(full);

        var relative = Path.GetRelativePath(root, full);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
            return false;

        var segments = relative.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        var scanStart = ScanStartFor(root);

        // Walk the path from the root down: an excluded ancestor excludes everything
        // beneath it, so the first hit wins.
        for (var i = 0; i < segments.Length; i++)
        {
            var isDirectorySegment = i < segments.Length - 1 || isDirectory;
            if (isDirectorySegment && IsDeniedDirectoryName(segments[i]))
                return true;

            var candidate = Path.Combine(root, Path.Combine(segments[..(i + 1)]));
            if (MatchesGitIgnore(scanStart, candidate, isDirectorySegment) == true)
                return true;
        }

        return false;
    }

    private static bool IsDeniedDirectoryName(string name)
    {
        return name.StartsWith('.') || DeniedDirectoryNames.Contains(name);
    }

    /// <summary>Fallback for paths outside every registered root (no workspace context to
    ///     resolve <c>.gitignore</c> or relative segments against).</summary>
    private static bool IsGeneratedOutputPath(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{sep}.git{sep}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The longest registered root containing <paramref name="full" />.</summary>
    private string? FindRoot(string full)
    {
        string? best = null;
        foreach (var root in _roots.Keys)
        {
            if (
                !full.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                continue;

            if (best is null || root.Length > best.Length)
                best = root;
        }

        return best;
    }

    /// <summary>
    ///     Where the <c>.gitignore</c> chain starts for <paramref name="root" />: the
    ///     nearest ancestor (inclusive) holding a <c>.git</c> entry, so a workspace folder
    ///     opened on a subdirectory still honors the repository-root ignores. Falls back
    ///     to the root itself outside a repository.
    /// </summary>
    private string ScanStartFor(string root)
    {
        return _scanStarts.GetOrAdd(
            root,
            static r =>
            {
                var dir = r;
                while (dir is not null)
                {
                    try
                    {
                        if (
                            Directory.Exists(Path.Combine(dir, ".git"))
                            || File.Exists(Path.Combine(dir, ".git"))
                        )
                            return dir;
                    }
                    catch
                    {
                        break;
                    }

                    dir = Path.GetDirectoryName(dir);
                }

                return r;
            }
        );
    }

    /// <summary>
    ///     The verdict of the <c>.gitignore</c> chain covering <paramref name="candidate" />,
    ///     evaluated outermost-first so the innermost file wins (this is what anchors
    ///     <c>editor/zed/.gitignore</c>'s <c>/grammars</c> to <c>editor/zed/</c>).
    /// </summary>
    private bool? MatchesGitIgnore(string scanStart, string candidate, bool isDirectory)
    {
        var parent = Path.GetDirectoryName(candidate);
        if (parent is null)
            return null;

        var chain = new List<string>();
        var dir = parent;
        while (dir is not null)
        {
            chain.Add(dir);
            if (dir.Equals(scanStart, StringComparison.OrdinalIgnoreCase))
                break;
            dir = Path.GetDirectoryName(dir);
        }

        bool? verdict = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var rules = RulesFor(chain[i]);
            if (rules is null)
                continue;

            var relative = Path.GetRelativePath(chain[i], candidate)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

            var match = rules.Match(relative, isDirectory);
            if (match is not null)
                verdict = match;
        }

        return verdict;
    }

    /// <summary>The parsed <c>.gitignore</c> of <paramref name="directory" />, re-read when
    ///     the file's timestamp changes so mid-session edits take effect.</summary>
    private GitIgnoreRules? RulesFor(string directory)
    {
        var path = Path.Combine(directory, ".gitignore");

        DateTime stamp;
        try
        {
            var info = new FileInfo(path);
            stamp = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch
        {
            stamp = DateTime.MinValue;
        }

        if (_gitIgnoreCache.TryGetValue(directory, out var cached) && cached.Stamp == stamp)
            return cached.Rules;

        var rules = stamp == DateTime.MinValue ? null : GitIgnoreRules.Load(path);
        _gitIgnoreCache[directory] = new CachedRules(stamp, rules);
        return rules;
    }

    private static string Normalize(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
