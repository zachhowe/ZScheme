namespace ZScheme.Compiler.Codegen;

internal static class NameConverter
{
    internal static string SanitizeIdentifier(string name)
    {
        var sanitized = ReplaceSpecialChars(name);
        return ToCaseSegmented(sanitized, pascalCase: true);
    }

    internal static string SanitizeParameter(string name)
    {
        var sanitized = ReplaceSpecialChars(name);
        return ToCaseSegmented(sanitized, pascalCase: false);
    }

    internal static string ClassNameFromModuleName(string moduleName)
    {
        return ToCaseSegmented(moduleName, pascalCase: true) + "Module";
    }

    private static string ReplaceSpecialChars(string name)
    {
        return name
            .Replace("?", "_q")
            .Replace(">", "_gt")
            .Replace("|", "_pipe")
            .Replace("^", "");
    }

    private static string ToCaseSegmented(string name, bool pascalCase)
    {
        // Split on '/' — each segment becomes underscore-separated
        var slashSegments = name.Split('/');
        var parts = new List<string>();

        foreach (var segment in slashSegments)
        {
            if (segment.Length == 0)
                continue;

            // Split on '-' — each part gets capitalized
            var hyphenParts = segment.Split('-');
            var converted = string.Concat(
                hyphenParts
                    .Where(s => s.Length > 0)
                    .Select((s, i) =>
                    {
                        if (!pascalCase && i == 0 && parts.Count == 0)
                            return char.ToLowerInvariant(s[0]) + s[1..];
                        return char.ToUpperInvariant(s[0]) + s[1..];
                    }));

            if (converted.Length > 0)
                parts.Add(converted);
        }

        return string.Join("_", parts);
    }
}
