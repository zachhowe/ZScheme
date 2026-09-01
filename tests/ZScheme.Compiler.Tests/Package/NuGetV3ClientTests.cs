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

    private const string NupkgUrl = "https://fake.test/flat/my.pkg/1.0.0/my.pkg.1.0.0.nupkg";

    /// <summary>
    ///     Body of a nupkg, distinctive enough that a truncated copy fails the comparison.
    /// </summary>
    private static byte[] NupkgBody() =>
        [.. Enumerable.Range(0, 32 * 1024).Select(i => (byte)(i % 251))];

    /// <summary>No-logic fake content: reports arrival, waits to be released, then writes.
    ///     Holding every downloader inside its copy at once is what makes the overlap the
    ///     race needs deterministic.</summary>
    private sealed class GatedContent(byte[] body, Action onArrival, Task release) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context
        )
        {
            onArrival();
            await release;
            await stream.WriteAsync(body);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }

    /// <summary>No-logic fake content: writes a prefix, then fails the transfer.</summary>
    private sealed class TornContent(byte[] body) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context
        )
        {
            await stream.WriteAsync(body.AsMemory(0, body.Length / 2));
            throw new IOException("connection reset mid-transfer");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }

    /// <summary>
    ///     Regression: the nupkg cache is shared by every compile running on the machine — the
    ///     test assemblies <c>dotnet test</c> runs side by side, several <c>zs build</c>s. Copying
    ///     the response straight into the destination made concurrent downloaders collide ("the
    ///     process cannot access the file … because it is being used by another process") and let
    ///     a reader open the half-written archive that <c>File.Exists</c> had already counted as a
    ///     cache hit. Each download now lands by rename.
    /// </summary>
    [Fact]
    public async Task ConcurrentDownloadsOfOnePackageAllLandOneCompleteFile()
    {
        const int downloaders = 8;
        var body = NupkgBody();
        var dest = Path.Combine(_tempDir, "shared", "my.pkg.1.0.0.nupkg");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allArrived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var arrived = 0;
        void OnArrival()
        {
            if (Interlocked.Increment(ref arrived) == downloaders)
                allArrived.SetResult();
        }

        var clients = new List<NuGetV3Client>();
        var downloads = new List<Task>();
        for (var i = 0; i < downloaders; i++)
        {
            var handler = NewHandler();
            handler.Responses[NupkgUrl] = () =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new GatedContent(body, OnArrival, release.Task),
                };
            var client = NewClient(handler);
            clients.Add(client);
            downloads.Add(Task.Run(() => client.DownloadNupkgAsync("My.Pkg", "1.0.0", dest)));
        }

        try
        {
            // Release once every downloader is inside its copy, each holding its own staging
            // file -- or as soon as one has finished early, which under the old direct-write
            // meant it had already failed. Waiting only on the full count would hang there
            // instead of reporting the collision below.
            await Task.WhenAny(allArrived.Task, Task.WhenAny(downloads));
            release.SetResult();
            await Task.WhenAll(downloads);

            Assert.Equal(body, await File.ReadAllBytesAsync(dest));

            // Only the finished archive: every staging file was cleaned up after landing.
            Assert.Equal([dest], Directory.GetFiles(Path.GetDirectoryName(dest)!));
        }
        finally
        {
            foreach (var client in clients)
                client.Dispose();
        }
    }

    /// <summary>
    ///     A transfer that dies partway leaves nothing in the cache. Writing directly to the
    ///     destination left the truncated prefix there, and every later compile took it for a
    ///     cache hit.
    /// </summary>
    [Fact]
    public async Task DownloadFailingMidTransferLeavesNoCacheEntry()
    {
        var handler = NewHandler();
        handler.Responses[NupkgUrl] = () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new TornContent(NupkgBody()) };
        using var client = NewClient(handler);
        var dest = Path.Combine(_tempDir, "torn", "my.pkg.1.0.0.nupkg");

        var failure = await Record.ExceptionAsync(() =>
            client.DownloadNupkgAsync("My.Pkg", "1.0.0", dest)
        );

        Assert.NotNull(failure);
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(dest)!));
    }
}
