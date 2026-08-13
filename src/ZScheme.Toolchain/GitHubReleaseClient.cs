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

        // disposeHandler carries the only ownership question there is: an injected handler belongs
        // to the caller, and a shared one is the standard way to share a connection pool. The
        // wrapper itself is constructed here either way and is disposed here either way -- see
        // Dispose.
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        // Mandatory: the GitHub API rejects requests that do not identify themselves.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("zsup");

        // Replaced by the per-step budget below -- see NetworkTimeout for why this one cannot do
        // the job. Left infinite rather than merely generous so there is a single answer to "what
        // gives up, and when".
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    ///     How long one network step may make no progress: a request that never answers, or a
    ///     download whose connection goes quiet mid-stream.
    /// </summary>
    /// <remarks>
    ///     Per step rather than per call, which is what <see cref="HttpClient.Timeout" /> could not
    ///     be. It bounds a streamed response from the request until the last byte is read, so with
    ///     <c>ResponseHeadersRead</c> it covers the whole download: a toolchain archive over a link
    ///     slower than roughly 1 Mbps aborted part-way through a transfer that was making perfectly
    ///     steady progress, reported as "the request was canceled due to the configured
    ///     HttpClient.Timeout", with nothing the user could raise. Settable so tests can drive both
    ///     halves without waiting minutes.
    /// </remarks>
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The newest published release.</summary>
    public async Task<ReleaseRef> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        var url = $"{_apiBaseUrl}/repos/{_repository}/releases/latest";

        using var step = StartStep(cancellationToken);
        GitHubRelease? release;
        try
        {
            release = await _http.GetFromJsonAsync(
                url,
                GitHubJsonContext.Default.GitHubRelease,
                step.Token
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Stalled(url);
        }

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
        var url = GetAssetUrl(release, assetName);

        using var step = StartStep(cancellationToken);
        try
        {
            return await _http.GetStringAsync(url, step.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Stalled(url);
        }
    }

    /// <summary>
    ///     Downloads an asset to <paramref name="destPath" />, hashing as it streams.
    /// </summary>
    /// <remarks>
    ///     Written to a <c>.part</c> file first and only renamed once complete, so an interrupted
    ///     download can never be mistaken for a finished one. That file is private to this call
    ///     rather than derived from <paramref name="destPath" /> alone, because two zsup processes
    ///     fetching the same asset would otherwise stream into one file and hash each other's
    ///     bytes.
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

        // Private to this download, the way every other transient zsup writes is -- a CI job
        // installing a pinned version twice over one home, or an editor's install racing the
        // user's, otherwise share this file, and each hashes whatever the other left in it. The
        // guid goes before the extension rather than after so the name still ends in ".part": a
        // destPath directly in downloads/ is then reclaimed by ToolchainInstaller.SweepTransients'
        // trailing-".part" file rule, and this method is public API that does not require its
        // destPath to be anywhere in particular. Through zsup's own callers the file lands inside a
        // .dl-<guid> slot instead, and the slot-directory rule is what sweeps it -- the file rule
        // enumerates downloads/ without recursing, so it never sees one there.
        var partPath = $"{destPath}.{Guid.NewGuid().ToString("N")[..8]}.part";

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        string digest;
        try
        {
            using var response = await GetHeadersAsync(url, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new FileNotFoundException(
                    $"no such release asset: {assetName} (looked at {url})",
                    assetName
                );

            response.EnsureSuccessStatusCode();

            // Scoped rather than held to the end of the try, because the hash below reopens the
            // .part and cannot do that while this handle is still on it.
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(partPath))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    // Each read gets its own budget, so a download only fails for going quiet --
                    // never for taking a long time while bytes keep arriving.
                    int read;
                    using (var step = StartStep(cancellationToken))
                    {
                        try
                        {
                            read = await source.ReadAsync(buffer, step.Token);
                        }
                        catch (OperationCanceledException)
                            when (!cancellationToken.IsCancellationRequested)
                        {
                            throw Stalled(url);
                        }
                    }

                    if (read <= 0)
                        break;

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                    progress?.Report(total);
                }
            }

            // Inside the try, not after it: a sharing violation here -- on the .part just closed,
            // or on an existing destPath -- fails a download whose every byte arrived and whose
            // SHA-256 was computed successfully, and outside the try it orphaned the .part with no
            // cleanup at all. Nothing would then reclaim it until the slot it sits in ages out.
            digest = Checksums.ComputeSha256(partPath);
            await MoveIntoPlaceAsync(partPath, destPath, cancellationToken);
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

        return digest;
    }

    /// <summary>Attempts made to rename a finished download into place.</summary>
    /// <remarks>
    ///     On Windows a scanner routinely opens a file the instant it is closed, and this one was
    ///     just written and then read end to end by the checksum — so the rename can meet a sharing
    ///     violation on a download that is complete and verified, and the install would fail for a
    ///     reason that has nothing to do with the release, the network, or the checksum. Such a hold
    ///     is measured in milliseconds. <c>ShimInstaller</c> and <c>ToolchainRegistry</c> both
    ///     already treat a lock on a freshly written file as an expected condition rather than an
    ///     error; this is the same judgement in the one place waiting is enough on its own.
    /// </remarks>
    private const int MoveAttempts = 5;

    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc cref="MoveAttempts" />
    private static async Task MoveIntoPlaceAsync(
        string partPath,
        string destPath,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(partPath, destPath, overwrite: true);
                return;
            }
            // Both, because Windows reports the two ends differently: a hold on the .part comes
            // back as a sharing-violation IOException, while one on an existing destPath makes
            // MoveFileEx fail with ERROR_ACCESS_DENIED and surfaces as UnauthorizedAccessException.
            catch (Exception e)
                when (e is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                await Task.Delay(MoveRetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>Sends the request and waits only for the headers, under its own budget.</summary>
    private async Task<HttpResponseMessage> GetHeadersAsync(
        string url,
        CancellationToken cancellationToken
    )
    {
        using var step = StartStep(cancellationToken);
        try
        {
            return await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, step.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Stalled(url);
        }
    }

    /// <summary>A token for one network step: the caller's, plus <see cref="NetworkTimeout" />.</summary>
    private CancellationTokenSource StartStep(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(NetworkTimeout);
        return cts;
    }

    /// <summary>
    ///     The failure a step's budget running out means, rather than the bare "the operation was
    ///     canceled" the token produces. Typed as <see cref="IOException" /> because that is what
    ///     every caller already catches for a transfer that did not finish.
    /// </summary>
    private IOException Stalled(string url)
    {
        return new IOException(
            $"no data from {url} for {NetworkTimeout.TotalSeconds:0}s; giving up"
        );
    }

    private static string? Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public void Dispose()
    {
        // Unconditionally: the wrapper is constructed here in both branches and owned here in both
        // branches, so skipping this leaks whatever it holds -- its own cancellation registrations
        // and the timer behind Timeout. Whether the *handler* goes with it is the disposeHandler
        // argument in the constructor, which is the only ownership question there is; a flag
        // restating it here would apply it to the wrong object.
        _http.Dispose();
    }
}
