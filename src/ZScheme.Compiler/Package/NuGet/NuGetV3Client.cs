using System.Text.Json;

namespace ZScheme.Compiler.Package.NuGet;

internal sealed class NuGetV3Client : INuGetV3Client
{
    private readonly HttpClient _http;
    private readonly string _serviceIndexUrl;
    private string? _packageBaseAddress;

    public NuGetV3Client(string serviceIndexUrl = "https://api.nuget.org/v3/index.json")
        : this(CreateDefaultClient(), serviceIndexUrl) { }

    // Test seam: lets tests supply an HttpClient over a fake handler instead of the network.
    internal NuGetV3Client(HttpClient http, string serviceIndexUrl)
    {
        _serviceIndexUrl = serviceIndexUrl;
        _http = http;
    }

    private static HttpClient CreateDefaultClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "ZScheme-Compiler/0.1");
        return http;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public async Task<string> GetPackageBaseAddressAsync()
    {
        if (_packageBaseAddress is not null)
            return _packageBaseAddress;

        var json = await _http.GetStringAsync(_serviceIndexUrl);
        using var doc = JsonDocument.Parse(json);

        foreach (var resource in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            var type = resource.GetProperty("@type").GetString();
            if (type is "PackageBaseAddress/3.0.0")
            {
                _packageBaseAddress = resource.GetProperty("@id").GetString()!.TrimEnd('/');
                return _packageBaseAddress;
            }
        }

        throw new InvalidOperationException(
            "NuGet service index does not contain PackageBaseAddress/3.0.0 resource"
        );
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string packageId)
    {
        var baseAddress = await GetPackageBaseAddressAsync();
        var lowerId = packageId.ToLowerInvariant();
        var url = $"{baseAddress}/{lowerId}/index.json";

        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return [];

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var versions = new List<string>();
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            var version = v.GetString();
            if (version is not null)
                versions.Add(version);
        }

        return versions;
    }

    public async Task DownloadNupkgAsync(string packageId, string version, string destinationPath)
    {
        var baseAddress = await GetPackageBaseAddressAsync();
        var lowerId = packageId.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        var url = $"{baseAddress}/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var dir = Path.GetDirectoryName(destinationPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        // Land the nupkg in one step. The cache is shared by every compile running on the
        // machine -- the test assemblies `dotnet test` runs side by side, several `zs build`s --
        // and copying straight into destinationPath let one process open the file another was
        // still filling ("used by another process"), or handed a reader the half-written archive
        // that File.Exists had already counted as a cache hit.
        var staging = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var fileStream = File.Create(staging))
            {
                await response.Content.CopyToAsync(fileStream);
            }

            File.Move(staging, destinationPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            // Someone else landed the same package first; their copy is as good as ours.
        }
        finally
        {
            try
            {
                File.Delete(staging);
            }
            catch (IOException)
            {
                // Best-effort cleanup of our own scratch file.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup of our own scratch file.
            }
        }
    }
}
