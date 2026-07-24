using System.Text;
using System.Text.RegularExpressions;

namespace ZScheme.LanguageServer.Analysis;

/// <summary>
///     The patterns of a single <c>.gitignore</c> file, compiled to regexes so the
///     workspace scan can skip the same files git does (generated trees like
///     <c>fuzz-runs/</c> or <c>dist/</c> are not source and must not reach the index).
/// </summary>
/// <remarks>
///     Supports the pattern syntax that matters for a working tree: comments, blank
///     lines, <c>\</c> escapes, <c>!</c> negation, trailing <c>/</c> (directory-only),
///     anchoring (a leading or embedded <c>/</c> anchors to the <c>.gitignore</c>'s own
///     directory; otherwise the pattern matches a basename at any depth), and the
///     <c>*</c> / <c>?</c> / <c>[…]</c> / <c>**</c> globs. Deliberately *not* supported:
///     <c>.git/info/exclude</c>, the global <c>core.excludesFile</c>, and any other
///     out-of-tree exclude source — this is a scan filter, not a git reimplementation.
///     Matching is case-insensitive, matching the rest of the server's path handling.
/// </remarks>
internal sealed class GitIgnoreRules
{
    private sealed record Pattern(Regex Regex, bool Negated, bool DirectoryOnly);

    private readonly List<Pattern> _patterns;

    private GitIgnoreRules(List<Pattern> patterns)
    {
        _patterns = patterns;
    }

    /// <summary>Reads and parses a <c>.gitignore</c>; null when it cannot be read.</summary>
    public static GitIgnoreRules? Load(string gitIgnorePath)
    {
        try
        {
            return Parse(File.ReadAllLines(gitIgnorePath));
        }
        catch
        {
            return null;
        }
    }

    public static GitIgnoreRules Parse(IEnumerable<string> lines)
    {
        var patterns = new List<Pattern>();
        foreach (var raw in lines)
        {
            var pattern = ParseLine(raw);
            if (pattern is not null)
                patterns.Add(pattern);
        }

        return new GitIgnoreRules(patterns);
    }

    /// <summary>
    ///     Whether this file ignores <paramref name="relativePath" /> (given with
    ///     <c>/</c> separators, relative to the <c>.gitignore</c>'s directory, no leading
    ///     slash). Null when no pattern applies; otherwise the last matching pattern wins,
    ///     so a later <c>!</c> line re-includes.
    /// </summary>
    public bool? Match(string relativePath, bool isDirectory)
    {
        bool? verdict = null;
        foreach (var pattern in _patterns)
        {
            if (pattern.DirectoryOnly && !isDirectory)
                continue;
            if (pattern.Regex.IsMatch(relativePath))
                verdict = !pattern.Negated;
        }

        return verdict;
    }

    private static Pattern? ParseLine(string raw)
    {
        var line = TrimTrailingUnescapedSpaces(raw);
        if (line.Length == 0 || line[0] == '#')
            return null;

        var negated = line[0] == '!';
        if (negated)
            line = line[1..];
        else if (
            line.StartsWith("\\#", StringComparison.Ordinal)
            || line.StartsWith("\\!", StringComparison.Ordinal)
        )
            line = line[1..];

        if (line.Length == 0)
            return null;

        var directoryOnly = line[^1] == '/';
        if (directoryOnly)
            line = line[..^1];

        if (line.Length == 0)
            return null;

        // A slash anywhere but the (already stripped) trailing position anchors the
        // pattern to the .gitignore's own directory; otherwise it matches a basename
        // at any depth.
        var anchored = line.Contains('/');
        if (line[0] == '/')
            line = line[1..];

        if (line.Length == 0)
            return null;

        var body = Translate(line);
        var regex = new Regex(
            anchored ? $"^{body}$" : $"^(?:.*/)?{body}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );

        return new Pattern(regex, negated, directoryOnly);
    }

    /// <summary>Trailing spaces are insignificant unless escaped with a backslash.</summary>
    private static string TrimTrailingUnescapedSpaces(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ')
        {
            var backslashes = 0;
            var i = end - 2;
            while (i >= 0 && line[i] == '\\')
            {
                backslashes++;
                i--;
            }

            if (backslashes % 2 == 1)
                break;
            end--;
        }

        return line[..end];
    }

    /// <summary>Translates a gitignore glob into a regex body (no anchors).</summary>
    private static string Translate(string glob)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            switch (c)
            {
                case '\\' when i + 1 < glob.Length:
                    sb.Append(Regex.Escape(glob[i + 1].ToString()));
                    i += 2;
                    continue;

                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                {
                    var atSegmentStart = i == 0 || glob[i - 1] == '/';
                    var followedBySlash = i + 2 < glob.Length && glob[i + 2] == '/';
                    if (atSegmentStart && followedBySlash)
                    {
                        // "**/" — zero or more leading path segments.
                        sb.Append("(?:[^/]+/)*");
                        i += 3;
                        continue;
                    }

                    if (atSegmentStart && i + 2 == glob.Length)
                    {
                        // Trailing "**" — everything below this point.
                        sb.Append(".*");
                        i += 2;
                        continue;
                    }

                    // "**" anywhere else degrades to a single "*", as git does.
                    sb.Append("[^/]*");
                    i += 2;
                    continue;
                }

                case '*':
                    sb.Append("[^/]*");
                    i++;
                    continue;

                case '?':
                    sb.Append("[^/]");
                    i++;
                    continue;

                case '[':
                {
                    var close = FindCharClassEnd(glob, i);
                    if (close < 0)
                    {
                        sb.Append("\\[");
                        i++;
                        continue;
                    }

                    var inner = glob[(i + 1)..close];
                    if (inner.StartsWith('!'))
                        inner = "^" + inner[1..];
                    sb.Append('[').Append(inner).Append(']');
                    i = close + 1;
                    continue;
                }

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    continue;
            }
        }

        return sb.ToString();
    }

    /// <summary>Index of the <c>]</c> closing the class opened at <paramref name="open" />,
    ///     or -1 when it is unterminated.</summary>
    private static int FindCharClassEnd(string glob, int open)
    {
        var j = open + 1;
        if (j < glob.Length && (glob[j] == '!' || glob[j] == '^'))
            j++;
        if (j < glob.Length && glob[j] == ']')
            j++;
        while (j < glob.Length && glob[j] != ']')
            j++;
        return j < glob.Length ? j : -1;
    }
}
