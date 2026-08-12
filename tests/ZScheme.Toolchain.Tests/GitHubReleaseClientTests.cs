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

    [Fact]
    public void GetAssetUrl_UsesTheReleaseDownloadPath()
    {
        using var client = new GitHubReleaseClient("owner/repo", baseUrlOverride: "");

        Assert.Equal(
            "https://github.com/owner/repo/releases/download/0.4.0/zscheme-0.4.0-linux-x64.tar.gz",
            client.GetAssetUrl("0.4.0", "zscheme-0.4.0-linux-x64.tar.gz")
        );
    }

    [Fact]
    public void GetAssetUrl_HonorsABaseUrlOverride()
    {
        using var client = new GitHubReleaseClient("owner/repo", "https://mirror.example/dist/");

        Assert.Equal(
            "https://mirror.example/dist/0.4.0/asset.zip",
            client.GetAssetUrl("0.4.0", "asset.zip")
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
    public async Task GetLatestVersionAsync_ReadsTheTagName()
    {
        var handler = new FakeHandler(_ => Ok("""{"tag_name":"0.4.0"}"""));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);

        Assert.Equal("0.4.0", await client.GetLatestVersionAsync());
        Assert.Equal(
            "https://api.github.com/repos/owner/repo/releases/latest",
            handler.Requests[0].RequestUri!.ToString()
        );
    }

    [Fact]
    public async Task GetLatestVersionAsync_StripsAVPrefix()
    {
        var handler = new FakeHandler(_ => Ok("""{"tag_name":"v1.2.3"}"""));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);

        Assert.Equal("1.2.3", await client.GetLatestVersionAsync());
    }

    [Fact]
    public async Task GetLatestVersionAsync_SendsAUserAgent()
    {
        // GitHub rejects API requests that do not identify themselves, so this is not optional.
        var handler = new FakeHandler(_ => Ok("""{"tag_name":"0.4.0"}"""));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);

        await client.GetLatestVersionAsync();

        Assert.NotEmpty(handler.Requests[0].Headers.UserAgent);
    }

    [Fact]
    public async Task GetLatestVersionAsync_NoTag_Throws()
    {
        var handler = new FakeHandler(_ => Ok("{}"));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLatestVersionAsync());
    }

    [Fact]
    public async Task DownloadAssetAsync_WritesTheFileAndReturnsItsDigest()
    {
        var handler = new FakeHandler(_ => Ok("payload"));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);
        using var home = new TempHome();
        var dest = Path.Combine(home.Path, "downloads", "asset.zip");

        var digest = await client.DownloadAssetAsync("0.4.0", "asset.zip", dest);

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

        await client.DownloadAssetAsync("0.4.0", "asset.zip", dest);

        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task DownloadAssetAsync_MissingAsset_ThrowsAHelpfulError()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);
        using var home = new TempHome();

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            client.DownloadAssetAsync("0.4.0", "missing.zip", Path.Combine(home.Path, "a.zip"))
        );

        Assert.Contains("missing.zip", error.Message);
    }

    [Fact]
    public async Task GetTextAssetAsync_FetchesFromTheReleaseUrl()
    {
        var handler = new FakeHandler(_ => Ok("digest  asset.zip"));
        using var client = new GitHubReleaseClient("owner/repo", "", handler);

        var content = await client.GetTextAssetAsync("0.4.0", Checksums.FileName);

        Assert.Equal("digest  asset.zip", content);
        Assert.EndsWith(
            "/releases/download/0.4.0/SHA256SUMS",
            handler.Requests[0].RequestUri!.ToString()
        );
    }
}
