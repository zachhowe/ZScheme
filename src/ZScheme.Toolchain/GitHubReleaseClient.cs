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

/// <summary>
///     A release identified both ways it gets used.
/// </summary>
/// <remarks>
///     The two are the same today, and the whole point of keeping them apart is the day they are
///     not: tolerating a <c>v</c> prefix while feeding the stripped value back in as the tag would
///     resolve <c>latest</c> successfully and then 404 on every asset.
/// </remarks>
/// <param name="Tag">The tag exactly as GitHub has it — the URL segment assets live under.</param>
/// <param name="Version">
///     The bare version the tag names. Appears in asset names, and becomes the toolchain name.
/// </param>
public sealed record ReleaseRef(string Tag, string Version)
{
    /// <summary>
    ///     A release the user named explicitly. What they typed is the tag; the version is that with
    ///     any <c>v</c> prefix removed, matching how a resolved <c>latest</c> is split.
    /// </summary>
    /// <remarks>
    ///     Taking the argument as the tag is what lets <c>install.sh</c> and <c>install.ps1</c> hand
    ///     their <c>TAG</c> straight through. Reconstructing the tag from a stripped version here
    ///     would 404 on every asset of a <c>v</c>-prefixed release, while resolving the release
    ///     itself would appear to succeed.
    /// </remarks>
    public static ReleaseRef Explicit(string tag)
    {
        return new ReleaseRef(tag, tag.StartsWith('v') ? tag[1..] : tag);
    }
}

/// <summary>Downloads release assets, resolving <c>latest</c> through the GitHub API.</summary>
public sealed class GitHubReleaseClient : IDisposable
{
    public const string DefaultRepository = "zachhowe/ZScheme";
    public const string DefaultApiBaseUrl = "https://api.github.com";

    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly string? _baseUrlOverride;
    private readonly string _apiBaseUrl;
    private readonly bool _ownsClient;

    /// <param name="apiBaseUrlOverride">
    ///     Where <c>latest</c> is resolved from. Separate from <paramref name="baseUrlOverride" />,
    ///     which only covers asset downloads: a mirrored or airgapped setup that overrides the
    ///     download base would otherwise still reach out to api.github.com and fail there, with
    ///     every asset it needs perfectly reachable.
    /// </param>
    /// <param name="handler">
    ///     Injected in tests so the whole client can be exercised without network access.
    /// </param>
    public GitHubReleaseClient(
        string? repository = null,
        string? baseUrlOverride = null,
        HttpMessageHandler? handler = null,
        string? apiBaseUrlOverride = null
    )
    {
        _repository =
            Blank(repository)
            ?? Blank(Environment.GetEnvironmentVariable("ZSCHEME_GITHUB_REPO"))
            ?? DefaultRepository;

        _baseUrlOverride =
            Blank(baseUrlOverride)
            ?? Blank(Environment.GetEnvironmentVariable("ZSCHEME_DIST_BASE_URL"));

        _apiBaseUrl = (
            Blank(apiBaseUrlOverride)
            ?? Blank(Environment.GetEnvironmentVariable("ZSCHEME_GITHUB_API_URL"))
            ?? DefaultApiBaseUrl
        ).TrimEnd('/');

        _ownsClient = handler is null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        // Mandatory: the GitHub API rejects requests that do not identify themselves.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("zsup");
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    /// <summary>The newest published release.</summary>
    public async Task<ReleaseRef> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        var url = $"{_apiBaseUrl}/repos/{_repository}/releases/latest";

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
        // not strand this client -- but keep the tag itself, because that is what the download URL
        // is built from.
        return new ReleaseRef(tag, tag.StartsWith('v') ? tag[1..] : tag);
    }

    /// <summary>Download URL for one asset of a release.</summary>
    public string GetAssetUrl(ReleaseRef release, string assetName)
    {
        return _baseUrlOverride is not null
            ? $"{_baseUrlOverride.TrimEnd('/')}/{release.Tag}/{assetName}"
            : $"https://github.com/{_repository}/releases/download/{release.Tag}/{assetName}";
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
        ReleaseRef release,
        string assetName,
        CancellationToken cancellationToken = default
    )
    {
        return await _http.GetStringAsync(GetAssetUrl(release, assetName), cancellationToken);
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
        ReleaseRef release,
        string assetName,
        string destPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var url = GetAssetUrl(release, assetName);
        var partPath = destPath + ".part";

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        if (File.Exists(partPath))
            File.Delete(partPath);

        try
        {
            using var response = await _http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );

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
        catch
        {
            // A .part file is never resumed, so leaving one behind just accumulates dead weight in
            // downloads/. ToolchainInstaller's sweep is the backstop for a hard kill, which cannot
            // run this.
            try
            {
                File.Delete(partPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best-effort, and the caller's failure is the interesting one.
            }

            throw;
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
