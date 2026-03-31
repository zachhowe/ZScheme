using ZScheme.Compiler.Diagnostics;

namespace ZScheme.Compiler.Package.NuGet;

internal sealed class NuGetDependencyGraph(INuGetV3Client client, string packageCacheRoot, DiagnosticBag diagnostics)
{
    private readonly Dictionary<string, string> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ResolvedPackage>> ResolveAsync(IReadOnlyList<NuGetDependency> roots)
    {
        var rootSpans = new Dictionary<string, SourceSpan>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, string Version, SourceSpan Span)>();

        foreach (var root in roots)
        {
            rootSpans.TryAdd(root.PackageId, root.Span);
            queue.Enqueue((root.PackageId, root.Version, root.Span));
        }

        while (queue.Count > 0)
        {
            var (id, version, span) = queue.Dequeue();

            if (_resolved.ContainsKey(id))
                continue;

            var nupkgPath = await EnsureDownloadedAsync(id, version, span);
            if (nupkgPath is null)
                continue;

            _resolved[id] = version;

            var nuspec = NupkgExtractor.ReadNuspec(nupkgPath);
            var transitiveDeps = SelectDependencyGroup(nuspec);

            foreach (var dep in transitiveDeps)
            {
                if (_resolved.ContainsKey(dep.Id))
                    continue;

                var parentSpan = rootSpans.GetValueOrDefault(id, span);
                var resolvedVersion = await ResolveVersionAsync(dep.Id, dep.VersionRange, parentSpan);
                if (resolvedVersion is not null)
                    queue.Enqueue((dep.Id, resolvedVersion, parentSpan));
            }
        }

        return _resolved
            .Select(kv => new ResolvedPackage(
                kv.Key,
                kv.Value,
                GetNupkgCachePath(kv.Key, kv.Value)))
            .ToList();
    }

    private async Task<string?> EnsureDownloadedAsync(string id, string version, SourceSpan span)
    {
        var path = GetNupkgCachePath(id, version);

        if (File.Exists(path))
            return path;

        try
        {
            await client.DownloadNupkgAsync(id, version, path);
            return path;
        }
        catch (HttpRequestException ex)
        {
            diagnostics.Error($"Failed to download NuGet package {id} {version}: {ex.Message}", span);
            return null;
        }
    }

    private async Task<string?> ResolveVersionAsync(string id, string versionRange, SourceSpan span)
    {
        // If it looks like an exact version (no brackets, no commas), use it directly
        if (!versionRange.Contains('[') && !versionRange.Contains('(') && !versionRange.Contains(','))
            return versionRange;

        try
        {
            var versions = await client.GetVersionsAsync(id);
            var best = VersionRangeParser.FindBestMatch(versionRange, versions);
            if (best is null)
                diagnostics.Error($"No version of {id} satisfies range '{versionRange}'", span);
            return best;
        }
        catch (HttpRequestException ex)
        {
            diagnostics.Error($"Failed to query versions for {id}: {ex.Message}", span);
            return null;
        }
    }

    private static IReadOnlyList<NuspecDependencyRef> SelectDependencyGroup(NuspecInfo nuspec)
    {
        if (nuspec.DependencyGroups.Count == 0)
            return [];

        // Try to find the best TFM match among dependency groups
        var tfms = nuspec.DependencyGroups
            .Where(g => g.TargetFramework is not null)
            .Select(g => g.TargetFramework!)
            .ToList();

        var bestTfm = TfmSelector.SelectBestTfm(tfms);
        if (bestTfm is not null)
        {
            var group = nuspec.DependencyGroups
                .FirstOrDefault(g => string.Equals(g.TargetFramework, bestTfm, StringComparison.OrdinalIgnoreCase));
            if (group is not null)
                return group.Dependencies;
        }

        // Fall back to the group with no TFM (applies to all frameworks)
        var noTfm = nuspec.DependencyGroups.FirstOrDefault(g => g.TargetFramework is null);
        return noTfm?.Dependencies ?? [];
    }

    private string GetNupkgCachePath(string id, string version)
    {
        return Path.Combine(packageCacheRoot, id.ToLowerInvariant(), version.ToLowerInvariant(),
            $"{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg");
    }
}
