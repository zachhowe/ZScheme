using Newtonsoft.Json.Linq;
using Xunit;
using ZScheme.LanguageServer.Tests.TestFixtures;

namespace ZScheme.LanguageServer.Tests;

/// <summary>
///     End-to-end tests against a real <c>zs-lsp</c> process. These cover the failures
///     that only exist at the protocol boundary: capabilities the server must advertise
///     statically (clients that ignore dynamic registration never open documents
///     otherwise), and a <c>didOpen</c> whose analysis must always produce diagnostics and
///     a navigable document within a deadline.
/// </summary>
public sealed class StdioServerTests
{
    /// <summary>Deadlines are generous — the point is to catch "never answers", not to
    ///     benchmark. A regression of the kind these guard against blows past any budget.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    private const string Source = """
        (module stdio-probe)
        (define (square [x : Int]) : Int (* x x))
        (define (twice [n : Int]) : Int (square n))
        """;

    [Fact]
    public void Server_AdvertisesNavigationCapabilities_EvenWhenClientClaimsDynamicRegistration()
    {
        using var ws = new TempPackageWorkspace(
            "spkg",
            new Dictionary<string, string> { ["probe.zs"] = Source }
        );
        using var client = StdioLspClient.Start(ws.Root);
        if (client is null)
            return; // server binary not built alongside the tests

        var dynamicClient = new JObject
        {
            ["textDocument"] = new JObject
            {
                ["synchronization"] = new JObject { ["dynamicRegistration"] = true },
                ["definition"] = new JObject { ["dynamicRegistration"] = true },
                ["declaration"] = new JObject { ["dynamicRegistration"] = true },
            },
        };

        var result = client.Initialize(ws.Root, dynamicClient);
        var capabilities = (JObject)result["capabilities"]!;

        // Without static advertisement a client like Zed never sends didOpen at all.
        Assert.NotNull(capabilities["textDocumentSync"]);
        Assert.NotNull(capabilities["definitionProvider"]);
        Assert.NotNull(capabilities["declarationProvider"]);
    }

    [Fact]
    public void DidOpen_PublishesDiagnostics_AndDefinitionAnswersWithinDeadline()
    {
        using var ws = new TempPackageWorkspace(
            "spkg2",
            new Dictionary<string, string> { ["probe.zs"] = Source }
        );
        using var client = StdioLspClient.Start(ws.Root);
        if (client is null)
            return;

        client.Initialize(ws.Root);
        var uri = ws.UriOf("probe.zs");
        client.DidOpen(uri, Source);

        // A didOpen that throws or never returns publishes nothing — the regression that
        // made every navigation request silently answer "no result".
        var diagnostics = client.AwaitDiagnostics(uri, Deadline);
        Assert.NotNull(diagnostics);

        // Cursor on the call to "square" in twice's body (0-based line 2).
        var (line, col) = LspTestSession.Locate(Source, "square", occurrence: 2);
        var result = client.PositionRequest(
            "textDocument/definition",
            uri,
            line - 1,
            col - 1,
            Deadline
        );

        Assert.NotNull(result);
        Assert.Equal(1, (int)result["range"]!["start"]!["line"]!);
    }

    /// <summary>A client that pipelines its startup sends didOpen while the server is still
    ///     handling initialize. The stock receiver discards it, leaving the document
    ///     permanently unanalysed with nothing but a stderr line to say so — see
    ///     <c>HandshakeAwareReceiver</c>.</summary>
    [Fact]
    public void DidOpen_ThatRacesTheHandshake_IsStillAnalysed()
    {
        using var ws = new TempPackageWorkspace(
            "spkg4",
            new Dictionary<string, string> { ["probe.zs"] = Source }
        );
        using var client = StdioLspClient.Start(ws.Root);
        if (client is null)
            return;

        // No read between these three writes, so didOpen lands while initialize is in flight.
        client.InitializePipelined(ws.Root);
        var uri = ws.UriOf("probe.zs");
        client.DidOpen(uri, Source);

        var diagnostics = client.AwaitDiagnostics(uri, Deadline);
        Assert.NotNull(diagnostics);

        // And the document is really there, not merely diagnosed: a dropped didOpen makes
        // every navigation request answer "no result".
        var (line, col) = LspTestSession.Locate(Source, "square", occurrence: 2);
        var result = client.PositionRequest(
            "textDocument/definition",
            uri,
            line - 1,
            col - 1,
            Deadline
        );

        Assert.NotNull(result);
        Assert.Equal(1, (int)result["range"]!["start"]!["line"]!);
    }

    [Fact]
    public void DebugFlag_IsAccepted_AndKeepsStdoutCleanForTheProtocol()
    {
        using var ws = new TempPackageWorkspace(
            "spkg3",
            new Dictionary<string, string> { ["probe.zs"] = Source }
        );
        // --debug routes verbose logging to stderr; anything leaking to stdout would
        // corrupt the JSON-RPC stream and this handshake would fail.
        using var client = StdioLspClient.Start(ws.Root, "--debug");
        if (client is null)
            return;

        var result = client.Initialize(ws.Root);

        Assert.NotNull(result["capabilities"]);
    }
}
