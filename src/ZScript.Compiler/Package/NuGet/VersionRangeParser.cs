namespace ZScript.Compiler.Package.NuGet;

internal static class VersionRangeParser
{
    /// <summary>
    ///     Finds the best matching version from <paramref name="available" /> that satisfies
    ///     the NuGet version <paramref name="range" />.
    ///     Supports: bare "1.0.0" (minimum inclusive), "[1.0.0]" (exact),
    ///     "[1.0.0, )" (minimum inclusive), "[1.0.0, 2.0.0)" (range).
    ///     Returns the highest satisfying version, or null if none match.
    /// </summary>
    public static string? FindBestMatch(string range, IReadOnlyList<string> available)
    {
        var (minInclusive, min, maxExclusive, max) = ParseRange(range.Trim());

        string? best = null;
        Version? bestParsed = null;

        foreach (var v in available)
        {
            if (!TryParseVersion(v, out var parsed))
                continue;

            if (min is not null)
            {
                var cmp = parsed.CompareTo(min);
                if (minInclusive ? cmp < 0 : cmp <= 0)
                    continue;
            }

            if (max is not null)
            {
                var cmp = parsed.CompareTo(max);
                if (maxExclusive ? cmp >= 0 : cmp > 0)
                    continue;
            }

            if (bestParsed is null || parsed > bestParsed)
            {
                best = v;
                bestParsed = parsed;
            }
        }

        return best;
    }

    private static (bool minInclusive, Version? min, bool maxExclusive, Version? max) ParseRange(string range)
    {
        // Bare version: "1.0.0" means >= 1.0.0
        if (!range.StartsWith('[') && !range.StartsWith('('))
        {
            if (TryParseVersion(range, out var v))
                return (true, v, false, null);
            return (true, null, false, null);
        }

        var minInclusive = range[0] == '[';
        var maxExclusive = range[^1] == ')';

        var inner = range[1..^1];
        var parts = inner.Split(',', 2);

        Version? min = null;
        Version? max = null;

        if (parts.Length == 1)
        {
            // Exact: [1.0.0]
            if (TryParseVersion(parts[0].Trim(), out var v))
                return (true, v, false, v);
        }
        else
        {
            var left = parts[0].Trim();
            var right = parts[1].Trim();

            if (left.Length > 0)
                TryParseVersion(left, out min);
            if (right.Length > 0)
                TryParseVersion(right, out max);
        }

        return (minInclusive, min, maxExclusive, max);
    }

    internal static bool TryParseVersion(string input, out Version version)
    {
        // Strip pre-release suffix (e.g., "1.0.0-beta1" → "1.0.0")
        var dashIndex = input.IndexOf('-');
        var clean = dashIndex >= 0 ? input[..dashIndex] : input;
        return Version.TryParse(clean, out version!);
    }
}
