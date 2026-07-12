using System.Net;
using System.Text;
using Xunit;
using ZScheme.Compiler.Package.NuGet;

namespace ZScheme.Compiler.Tests.Package;

public class NuGetV3ClientTests : IDisposable
{
    private const string IndexUrl = "https://fake.test/v3/index.json";

    private const string ServiceIndexJson = """
        {
          "resources": [
            { "@id": "https://fake.test/search/", "@type": "SearchQueryService" },
            { "@id": "https://fake.test/flat/", "@type": "PackageBaseAddress/3.0.0" }
          ]
        }
        """;

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"zs_nuget_test_{Guid.NewGuid():N}"
    );

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>No-logic fake per docs/MOCKS.md: URI-keyed canned responses + request recording.</summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public List<string> RequestedUris { get; } = [];

        public Dictionary<string, Func<HttpResponseMessage>> Responses { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var uri = request.RequestUri!.ToString();
            RequestedUris.Add(uri);
            var response = Responses.TryGetValue(uri, out var make)
                ? make()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static FakeHttpMessageHandler NewHandler(string indexJson = ServiceIndexJson)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Responses[IndexUrl] = () => Json(indexJson);
        return handler;
    }

    private static NuGetV3Client NewClient(FakeHttpMessageHandler handler)
    {
        return new NuGetV3Client(new HttpClient(handler), IndexUrl);
    }

    [Fact]
    public async Task PackageBaseAddressIsParsedAndTrailingSlashTrimmed()
    {
        using var client = NewClient(NewHandler());

        Assert.Equal("https://fake.test/flat", await client.GetPackageBaseAddressAsync());
    }

    [Fact]
    public async Task PackageBaseAddressIsCachedAfterFirstFetch()
    {
        var handler = NewHandler();
        using var client = NewClient(handler);

        await client.GetPackageBaseAddressAsync();
        await client.GetPackageBaseAddressAsync();

        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task MissingBaseAddressResourceThrows()
    {
        using var client = NewClient(NewHandler("""{ "resources": [] }"""));

        await Assert.ThrowsAsync<InvalidOperationException>(client.GetPackageBaseAddressAsync);
    }

    [Fact]
    public async Task GetVersionsLowercasesPackageIdInUrl()
    {
        var handler = NewHandler();
        handler.Responses["https://fake.test/flat/mixedcase.pkg/index.json"] = () =>
            Json("""{ "versions": ["1.0.0", "2.0.0-beta"] }""");
        using var client = NewClient(handler);

        var versions = await client.GetVersionsAsync("MixedCase.Pkg");

        Assert.Equal(["1.0.0", "2.0.0-beta"], versions);
        Assert.Contains("https://fake.test/flat/mixedcase.pkg/index.json", handler.RequestedUris);
    }

    [Fact]
    public async Task GetVersionsReturnsEmptyOnNonSuccessStatus()
    {
        // No canned response registered for the versions URL -> the fake returns 404.
        using var client = NewClient(NewHandler());

        var versions = await client.GetVersionsAsync("does.not.exist");

        Assert.Empty(versions);
    }

    [Fact]
    public async Task DownloadNupkgUsesFlatContainerUrlAndWritesBytes()
    {
        var handler = NewHandler();
        var body = new byte[] { 1, 2, 3, 4 };
        handler.Responses["https://fake.test/flat/my.pkg/1.0.0/my.pkg.1.0.0.nupkg"] = () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        using var client = NewClient(handler);

        var dest = Path.Combine(_tempDir, "nested", "my.pkg.nupkg");
        await client.DownloadNupkgAsync("My.Pkg", "1.0.0", dest);

        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task DownloadNupkgThrowsOn404AndWritesNoFile()
    {
        using var client = NewClient(NewHandler());
        var dest = Path.Combine(_tempDir, "missing.nupkg");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.DownloadNupkgAsync("no.such.pkg", "9.9.9", dest)
        );
        Assert.False(File.Exists(dest));
    }
}
