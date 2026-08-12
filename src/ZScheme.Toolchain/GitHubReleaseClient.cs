using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ZScheme.Toolchain;

/// <summary>Shape of the bit of the GitHub releases API we read.</summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }
}

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;

/// <summary>Downloads release assets, resolving <c>latest</c> through the GitHub API.</summary>
public sealed class GitHubReleaseClient : IDisposable
{
    public const string DefaultRepository = "zachhowe/ZScheme";

    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly string? _baseUrlOverride;
    private readonly bool _ownsClient;

    /// <param name="handler">
    ///     Injected in tests so the whole client can be exercised without network access.
    /// </param>
    public GitHubReleaseClient(
        string? repository = null,
        string? baseUrlOverride = null,
        HttpMessageHandler? handler = null
    )
    {
        _repository =
            Blank(repository)
            ?? Blank(Environment.GetEnvironmentVariable("ZSCHEME_GITHUB_REPO"))
            ?? DefaultRepository;

        _baseUrlOverride =
            Blank(baseUrlOverride)
            ?? Blank(Environment.GetEnvironmentVariable("ZSCHEME_DIST_BASE_URL"));

        _ownsClient = handler is null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        // Mandatory: the GitHub API rejects requests that do not identify themselves.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("zsup");
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    /// <summary>The tag of the newest published release.</summary>
    public async Task<string> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{_repository}/releases/latest";

        var release = await _http.GetFromJsonAsync(
            url,
            GitHubJsonContext.Default.GitHubRelease,
            cancellationToken
        );

        var tag = release?.TagName?.Trim();
        if (string.IsNullOrEmpty(tag))
            throw new InvalidOperationException(
                $"could not determine the latest release from {url}"
            );

        // Tags are bare versions (0.4.0). Tolerate a v-prefix so a future convention change does
        // not strand this client.
        return tag.StartsWith('v') ? tag[1..] : tag;
    }

    /// <summary>Download URL for one asset of a release.</summary>
    public string GetAssetUrl(string version, string assetName)
    {
        return _baseUrlOverride is not null
            ? $"{_baseUrlOverride.TrimEnd('/')}/{version}/{assetName}"
            : $"https://github.com/{_repository}/releases/download/{version}/{assetName}";
    }

    /// <summary>Asset name for a toolchain archive.</summary>
    public static string ToolchainAssetName(string version, string rid)
    {
        return $"zscheme-{version}-{rid}{RuntimeIdentifier.ArchiveExtension(rid)}";
    }

    /// <summary>Asset name for the zsup binary itself.</summary>
    public static string ZsupAssetName(string version, string rid)
    {
        return $"zsup-{version}-{rid}{RuntimeIdentifier.ArchiveExtension(rid)}";
    }

    /// <summary>Fetches a small text asset, such as <c>SHA256SUMS</c>.</summary>
    public async Task<string> GetTextAssetAsync(
        string version,
        string assetName,
        CancellationToken cancellationToken = default
    )
    {
        return await _http.GetStringAsync(GetAssetUrl(version, assetName), cancellationToken);
    }

    /// <summary>
    ///     Downloads an asset to <paramref name="destPath" />, hashing as it streams.
    /// </summary>
    /// <remarks>
    ///     Written to a <c>.part</c> file first and only renamed once complete, so an interrupted
    ///     download can never be mistaken for a finished one.
    /// </remarks>
    /// <returns>The SHA-256 of what was downloaded.</returns>
    public async Task<string> DownloadAssetAsync(
        string version,
        string assetName,
        string destPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var url = GetAssetUrl(version, assetName);
        var partPath = destPath + ".part";

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        if (File.Exists(partPath))
            File.Delete(partPath);

        using (
            var response = await _http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            )
        )
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new FileNotFoundException(
                    $"no such release asset: {assetName} (looked at {url})",
                    assetName
                );

            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(partPath);

            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
                progress?.Report(total);
            }
        }

        var digest = Checksums.ComputeSha256(partPath);
        File.Move(partPath, destPath, overwrite: true);
        return digest;
    }

    private static string? Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
