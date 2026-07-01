using System.IO.Compression;
using System.Xml.Linq;

namespace ZScheme.Compiler.Package.NuGet;

internal static class NupkgExtractor
{
    /// <summary>
    ///     Extracts DLLs from the best-matching TFM folder in the nupkg into <paramref name="targetDir" />.
    ///     Returns the list of extracted DLL file paths.
    /// </summary>
    public static IReadOnlyList<string> ExtractDlls(string nupkgPath, string targetDir)
    {
        using var zip = ZipFile.OpenRead(nupkgPath);

        var tfmFolders = zip
            .Entries.Where(e =>
                e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            )
            .Select(e =>
            {
                var parts = e.FullName.Split('/');
                return parts.Length >= 3 ? parts[1] : null;
            })
            .Where(t => t is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var bestTfm = TfmSelector.SelectBestTfm(tfmFolders!);
        if (bestTfm is null)
            return [];

        var prefix = $"lib/{bestTfm}/";
        var extracted = new List<string>();

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            var fileName = Path.GetFileName(entry.FullName);
            var destPath = Path.Combine(targetDir, fileName);
            entry.ExtractToFile(destPath, true);
            extracted.Add(destPath);
        }

        return extracted;
    }

    /// <summary>
    ///     Reads the .nuspec metadata from a nupkg file without full extraction.
    /// </summary>
    public static NuspecInfo ReadNuspec(string nupkgPath)
    {
        using var zip = ZipFile.OpenRead(nupkgPath);

        var nuspecEntry = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
            && !e.FullName.Contains('/')
        );

        if (nuspecEntry is null)
            return new NuspecInfo("", "", []);

        using var stream = nuspecEntry.Open();
        var doc = XDocument.Load(stream);
        return ParseNuspec(doc);
    }

    private static NuspecInfo ParseNuspec(XDocument doc)
    {
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var metadata = doc.Root?.Element(ns + "metadata");
        if (metadata is null)
            return new NuspecInfo("", "", []);

        var id = metadata.Element(ns + "id")?.Value ?? "";
        var version = metadata.Element(ns + "version")?.Value ?? "";

        var groups = new List<NuspecDependencyGroup>();
        var dependencies = metadata.Element(ns + "dependencies");
        if (dependencies is not null)
        {
            // Format 1: <dependencies><group targetFramework="...">...</group></dependencies>
            var groupElements = dependencies.Elements(ns + "group");
            foreach (var group in groupElements)
            {
                var tfm = group.Attribute("targetFramework")?.Value;
                var deps = ParseDependencyElements(group, ns);
                groups.Add(new NuspecDependencyGroup(NormalizeTfm(tfm), deps));
            }

            // Format 2: <dependencies><dependency .../></dependencies> (no groups)
            if (!groups.Any())
            {
                var deps = ParseDependencyElements(dependencies, ns);
                if (deps.Count > 0)
                    groups.Add(new NuspecDependencyGroup(null, deps));
            }
        }

        return new NuspecInfo(id, version, groups);
    }

    private static IReadOnlyList<NuspecDependencyRef> ParseDependencyElements(
        XElement parent,
        XNamespace ns
    )
    {
        return parent
            .Elements(ns + "dependency")
            .Select(d => new NuspecDependencyRef(
                d.Attribute("id")?.Value ?? "",
                d.Attribute("version")?.Value ?? ""
            ))
            .Where(d => d.Id.Length > 0)
            .ToList();
    }

    /// <summary>
    ///     Normalizes NuGet TFM strings like ".NETStandard,Version=v2.0" to "netstandard2.0" etc.
    /// </summary>
    private static string? NormalizeTfm(string? tfm)
    {
        if (tfm is null)
            return null;

        // Already short-form
        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase) && !tfm.Contains(','))
            return tfm.ToLowerInvariant();

        // Long-form: ".NETStandard,Version=v2.0" → "netstandard2.0"
        if (tfm.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase))
        {
            var ver = ExtractVersionFromLongForm(tfm);
            return ver is not null ? $"netstandard{ver}" : null;
        }

        // Long-form: ".NETCoreApp,Version=v8.0" → "net8.0"
        if (tfm.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase))
        {
            var ver = ExtractVersionFromLongForm(tfm);
            return ver is not null ? $"net{ver}" : null;
        }

        return tfm.ToLowerInvariant();
    }

    private static string? ExtractVersionFromLongForm(string tfm)
    {
        var vIdx = tfm.IndexOf("Version=v", StringComparison.OrdinalIgnoreCase);
        if (vIdx < 0)
            return null;
        return tfm[(vIdx + "Version=v".Length)..];
    }
}
