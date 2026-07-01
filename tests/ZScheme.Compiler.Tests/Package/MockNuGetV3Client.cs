using ZScheme.Compiler.Package.NuGet;

namespace ZScheme.Compiler.Tests.Package;

internal sealed class MockNuGetV3Client : INuGetV3Client
{
    // Call recording
    public List<string> GetVersionsCalls { get; } = [];
    public List<(string PackageId, string Version, string DestinationPath)> DownloadCalls { get; } =
    [];
    public int GetPackageBaseAddressCalls { get; private set; }

    // Configurable results
    public Dictionary<string, IReadOnlyList<string>> Versions { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Action<string, string, string>? OnDownload { get; set; }

    public Task<string> GetPackageBaseAddressAsync()
    {
        GetPackageBaseAddressCalls++;
        return Task.FromResult("https://api.nuget.org/v3-flatcontainer");
    }

    public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId)
    {
        GetVersionsCalls.Add(packageId);

        if (Versions.TryGetValue(packageId, out var versions))
            return Task.FromResult(versions);

        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task DownloadNupkgAsync(string packageId, string version, string destinationPath)
    {
        DownloadCalls.Add((packageId, version, destinationPath));
        OnDownload?.Invoke(packageId, version, destinationPath);
        return Task.CompletedTask;
    }

    public void Dispose() { }

    public void ClearTracking()
    {
        GetVersionsCalls.Clear();
        DownloadCalls.Clear();
        GetPackageBaseAddressCalls = 0;
    }
}
