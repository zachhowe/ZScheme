using System.Text;

namespace ZScheme.Formatter;

public static class EditorConfigParser
{
    public static FormattingOptions? TryParse(string filePath)
    {
        var settings = CollectSettings(filePath);
        return settings.Count == 0 ? null : ApplySettings(settings, FormattingOptions.Default);
    }

    /// <summary>
    /// Overlays the <c>.editorconfig</c> settings found above <paramref name="filePath" /> onto
    /// <paramref name="baseOptions" />, returning <paramref name="baseOptions" /> unchanged when there
    /// are none. Taking a base lets a caller slot lower-precedence settings (the language server passes
    /// the client's <c>tabSize</c>/<c>insertSpaces</c>) underneath the project's <c>.editorconfig</c>.
    /// </summary>
    public static FormattingOptions TryParse(string filePath, FormattingOptions baseOptions)
    {
        var settings = CollectSettings(filePath);
        return settings.Count == 0 ? baseOptions : ApplySettings(settings, baseOptions);
    }

    private static Dictionary<string, string> CollectSettings(string filePath)
    {
        var directory =
            Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();

        var allSettings = new Dictionary<string, string>();

        for (var dir = directory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            var editorConfigPath = Path.Combine(dir, ".editorconfig");
            if (!File.Exists(editorConfigPath))
                continue;

            var fileSettings = ParseFile(editorConfigPath);
            MergeSettings(allSettings, fileSettings);

            if (
                fileSettings.ContainsKey("root")
                && fileSettings["root"]?.ToLowerInvariant() == "true"
            )
                break;
        }

        return allSettings;
    }

    private static Dictionary<string, string> ParseFile(string path)
    {
        var settings = new Dictionary<string, string>();
        var lines = File.ReadAllLines(path);
        bool inMatchingSection = false;
        string? currentPattern = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.StartsWith(";") || line.StartsWith("#"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                var sectionName = line.Trim('[', ']').Trim();
                inMatchingSection = sectionName is "root" or "top";
                if (!inMatchingSection)
                {
                    currentPattern = sectionName;
                    inMatchingSection = MatchesZSchemeFile(sectionName);
                }

                continue;
            }

            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
                continue;

            var key = line[..eqIndex].Trim().ToLowerInvariant();
            var value = line[(eqIndex + 1)..].Trim();

            if (inMatchingSection)
                settings[key] = value;
        }

        return settings;
    }

    private static bool MatchesZSchemeFile(string pattern)
    {
        // Check for glob patterns with * or ?
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            return pattern.IndexOf(".zs", StringComparison.Ordinal) >= 0
                || pattern.IndexOf("zscheme", StringComparison.Ordinal) >= 0;
        }

        // Check for character class patterns like [zs]
        if (pattern.Contains('[') && pattern.Contains(']'))
        {
            var inner = pattern.Trim('[', ']');
            return inner.Contains("zs") || inner.Contains("scheme");
        }

        // Check for brace expansion like {zs,zscheme} or {.zs,.zscheme}
        if (pattern.Contains('{') && pattern.Contains('}'))
        {
            var braceStart = pattern.IndexOf('{');
            var braceEnd = pattern.IndexOf('}', braceStart);
            var inside = pattern.Substring(braceStart + 1, braceEnd - braceStart - 1);
            var variants = inside.Split(',', StringSplitOptions.RemoveEmptyEntries);
            return variants.Any(v => v.Contains("zs") || v.Contains("scheme"));
        }

        return false;
    }

    private static void MergeSettings(
        Dictionary<string, string> target,
        Dictionary<string, string> source
    )
    {
        foreach (var (key, value) in source)
            target[key] = value;
    }

    private static FormattingOptions ApplySettings(
        Dictionary<string, string> settings,
        FormattingOptions baseOptions
    )
    {
        var options = baseOptions;

        if (settings.TryGetValue("indent_size", out var indentSize))
        {
            if (indentSize == "tab")
                options = options with { UseTabs = true, IndentSize = 4 };
            else if (int.TryParse(indentSize, out var size) && size > 0)
                options = options with { IndentSize = size };
        }

        if (settings.TryGetValue("indent_style", out var indentStyle))
        {
            options = options with { UseTabs = indentStyle.Trim().ToLowerInvariant() == "tab" };
        }

        if (settings.TryGetValue("insert_final_newline", out var finalNewline))
        {
            options = options with
            {
                InsertFinalNewline = finalNewline.Trim().ToLowerInvariant() == "true",
            };
        }

        if (settings.TryGetValue("trim_trailing_whitespace", out var trimWhitespace))
        {
            options = options with
            {
                TrimTrailingWhitespace = trimWhitespace.Trim().ToLowerInvariant() == "true",
            };
        }

        return options;
    }
}
