using System.Net;
using Xunit;

namespace ZScheme.Toolchain.Tests;

/// <summary>
///     Exercised through an injected handler, so the whole client is covered without touching the
///     network.
/// </summary>
public sealed class GitHubReleaseClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private static readonly ReleaseRef Release040 = ReleaseRef.Explicit("0.4.0");

    [Fact]
    public void GetAssetUrl_UsesTheReleaseDownloadPath()
    {
        using var client = new GitHubReleaseClient("owner/repo", baseUrlOverride: "");

        Assert.Equal(
            "https://github.com/owner/repo/releases/download/0.4.0/zscheme-0.4.0-linux-x64.tar.gz",
            client.GetAssetUrl(Release040, "zscheme-0.4.0-linux-x64.tar.gz")
        );
    }

    [Fact]
    public void GetAssetUrl_HonorsABaseUrlOverride()
    {
        using var client = new GitHubReleaseClient("owner/repo", "https://mirror.example/dist/");

        Assert.Equal(
            "https://mirror.example/dist/0.4.0/asset.zip",
            client.GetAssetUrl(Release040, "asset.zip")
        );
    }

    [Fact]
    public void GetAssetUrl_UsesTheTagRatherThanTheVersion()
    {
        // The whole reason the two are carried separately: a v-prefixed tag resolves fine, and
        // then every asset URL built from the stripped version would 404.
        using var client = new GitHubReleaseClient("owner/repo", baseUrlOverride: "");

        Assert.Equal(
            "https://github.com/owner/repo/releases/download/v1.2.3/asset.zip",
            client.GetAssetUrl(new ReleaseRef("v1.2.3", "1.2.3"), "asset.zip")
        );
    }

    [Theory]
    [InlineData("0.4.0", "win-x64", "zscheme-0.4.0-win-x64.zip")]
    [InlineData("0.4.0", "linux-arm64", "zscheme-0.4.0-linux-arm64.tar.gz")]
    [InlineData("1.0.0-rc.1", "osx-arm64", "zscheme-1.0.0-rc.1-osx-arm64.tar.gz")]
    public void ToolchainAssetName_MatchesWhatPublishProduces(
        string version,
        string rid,
        string expected
    )
    {
        Assert.Equal(expected, GitHubReleaseClient.ToolchainAssetName(version, rid));
    }

    [Theory]
    [InlineData("0.4.0", "win-x64", "zsup-0.4.0-win-x64.zip")]
    [InlineData("0.4.0", "linux-x64", "zsup-0.4.0-linux-x64.tar.gz")]
    public void ZsupAssetName_MatchesWhatPublishProduces(
        string version,
        string rid,
        string expected
    )
    {
        Assert.Equal(expected, GitHubReleaseClient.ZsupAssetName(version, rid));
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ReadsTheTagName()
    {
        var handler = new FakeHandler(_ => Ok("""{"tag_name":"0.4.0"}"""));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);

        Assert.Equal(new ReleaseRef("0.4.0", "0.4.0"), await client.GetLatestReleaseAsync());
        Assert.Equal(
            "https://api.github.com/repos/owner/repo/releases/latest",
            handler.Requests[0].RequestUri!.ToString()
        );
    }

    [Fact]
    public async Task GetLatestReleaseAsync_StripsAVPrefixFromTheVersionButKeepsTheTag()
    {
        var handler = new FakeHandler(_ => Ok("""{"tag_name":"v1.2.3"}"""));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);

        var release = await client.GetLatestReleaseAsync();

        Assert.Equal("1.2.3", release.Version);
        Assert.Equal("v1.2.3", release.Tag);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_SendsAUserAgent()
    {
        // GitHub rejects API requests that do not identify themselves, so this is not optional.
        var handler = new FakeHandler(_ => Ok("""{"tag_name":"0.4.0"}"""));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);

        await client.GetLatestReleaseAsync();

        Assert.NotEmpty(handler.Requests[0].Headers.UserAgent);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_NoTag_Throws()
    {
        var handler = new FakeHandler(_ => Ok("{}"));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLatestReleaseAsync());
    }

    [Fact]
    public async Task DownloadAssetAsync_WritesTheFileAndReturnsItsDigest()
    {
        var handler = new FakeHandler(_ => Ok("payload"));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);
        using var home = new TempHome();
        var dest = Path.Combine(home.Path, "downloads", "asset.zip");

        var digest = await client.DownloadAssetAsync(Release040, "asset.zip", dest);

        Assert.Equal("payload", File.ReadAllText(dest));
        Assert.Equal(Checksums.ComputeSha256(dest), digest);
    }

    [Fact]
    public async Task DownloadAssetAsync_LeavesNoPartFileBehind()
    {
        var handler = new FakeHandler(_ => Ok("payload"));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);
        using var home = new TempHome();
        var dest = Path.Combine(home.Path, "downloads", "asset.zip");

        await client.DownloadAssetAsync(Release040, "asset.zip", dest);

        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task DownloadAssetAsync_MissingAsset_ThrowsAHelpfulError()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);
        using var home = new TempHome();

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            client.DownloadAssetAsync(Release040, "missing.zip", Path.Combine(home.Path, "a.zip"))
        );

        Assert.Contains("missing.zip", error.Message);
    }

    [Fact]
    public async Task GetTextAssetAsync_FetchesFromTheReleaseUrl()
    {
        var handler = new FakeHandler(_ => Ok("digest  asset.zip"));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);

        var content = await client.GetTextAssetAsync(Release040, Checksums.FileName);

        Assert.Equal("digest  asset.zip", content);
        Assert.EndsWith(
            "/releases/download/0.4.0/SHA256SUMS",
            handler.Requests[0].RequestUri!.ToString()
        );
    }
}
