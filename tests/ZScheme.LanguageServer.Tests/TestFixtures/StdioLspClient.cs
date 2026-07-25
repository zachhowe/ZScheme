using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace ZScheme.LanguageServer.Tests.TestFixtures;

/// <summary>
///     A minimal LSP client that speaks the wire protocol to a real <c>zs-lsp</c> process.
///     In-process tests exercise <see cref="ZScheme.LanguageServer.Analysis.AnalysisService" />
///     directly and so cannot catch protocol-level regressions — a capability the server
///     fails to advertise, or a notification handler that throws and takes the document
///     with it. Those only show up when something actually drives the binary.
/// </summary>
internal sealed class StdioLspClient : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly object _writeLock = new();
    private int _nextId = 1;

    private StdioLspClient(Process process)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdout = process.StandardOutput.BaseStream;
    }

    /// <summary>Locates the built <c>zs-lsp</c> next to the test assembly's own output —
    ///     the test project references the server project, so it is published alongside.
    ///     Returns null when it cannot be found, letting callers skip rather than fail.</summary>
    public static string? FindServerBinary()
    {
        var name = OperatingSystem.IsWindows() ? "zs-lsp.exe" : "zs-lsp";
        var candidate = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(candidate) ? candidate : null;
    }

    public static StdioLspClient? Start(string workingDirectory, params string[] args)
    {
        var exe = FindServerBinary();
        if (exe is null)
            return null;

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var process = Process.Start(psi);
        return process is null ? null : new StdioLspClient(process);
    }

    public JObject Initialize(string rootPath, JObject? capabilities = null)
    {
        var id = SendInitialize(rootPath, capabilities);
        var response = AwaitResponse(id, TimeSpan.FromSeconds(60));
        Notify("initialized", new JObject());
        return (JObject)response["result"]!;
    }

    /// <summary>Sends initialize and initialized without waiting for the initialize response,
    ///     the way a client that pipelines its startup does. Anything sent straight after
    ///     races the server's receiver, which reads ahead of the handling of what it has
    ///     already read — see <c>HandshakeAwareReceiver</c>. The initialize response is left
    ///     in the stream for a later read to skip past.</summary>
    public void InitializePipelined(string rootPath, JObject? capabilities = null)
    {
        SendInitialize(rootPath, capabilities);
        Notify("initialized", new JObject());
    }

    private int SendInitialize(string rootPath, JObject? capabilities)
    {
        return Request(
            "initialize",
            new JObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = LspUri.Of(rootPath),
                ["capabilities"] = capabilities ?? new JObject(),
            }
        );
    }

    public void DidOpen(string uri, string text)
    {
        Notify(
            "textDocument/didOpen",
            new JObject
            {
                ["textDocument"] = new JObject
                {
                    ["uri"] = uri,
                    ["languageId"] = "zscheme",
                    ["version"] = 1,
                    ["text"] = text,
                },
            }
        );
    }

    /// <summary>Sends a position request and returns its <c>result</c> (possibly null).</summary>
    public JToken? PositionRequest(
        string method,
        string uri,
        int line,
        int character,
        TimeSpan timeout
    )
    {
        var id = Request(
            method,
            new JObject
            {
                ["textDocument"] = new JObject { ["uri"] = uri },
                ["position"] = new JObject { ["line"] = line, ["character"] = character },
            }
        );
        return AwaitResponse(id, timeout)["result"];
    }

    /// <summary>Waits for a <c>publishDiagnostics</c> notification for
    ///     <paramref name="uri" />, or null if none arrives in time.</summary>
    public JArray? AwaitDiagnostics(string uri, TimeSpan timeout)
    {
        // Compare parsed URIs, not raw strings: the server re-serializes through DocumentUri, which
        // lower-cases the Windows drive letter, so an exact string match against a System.Uri-built
        // expectation never fires and the wait burns the whole timeout instead of failing fast.
        var expected = DocumentUri.Parse(uri);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var message = ReadMessage(deadline - DateTime.UtcNow);
            if (message is null)
                return null;
            if ((string?)message["method"] != "textDocument/publishDiagnostics")
                continue;
            if ((string?)message["params"]?["uri"] is not { } published)
                continue;
            if (DocumentUri.Parse(published) == expected)
                return (JArray?)message["params"]!["diagnostics"];
        }

        return null;
    }

    private int Request(string method, JObject parameters)
    {
        var id = Interlocked.Increment(ref _nextId);
        Write(
            new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            }
        );
        return id;
    }

    private void Notify(string method, JObject parameters)
    {
        Write(
            new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters,
            }
        );
    }

    private JObject AwaitResponse(int id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var message = ReadMessage(deadline - DateTime.UtcNow);
            if (message is null)
                throw new InvalidOperationException($"server closed while awaiting id {id}");

            // Server-to-client requests must be answered or the server may block.
            if (message["id"] is not null && message["method"] is not null)
            {
                Write(
                    new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = message["id"],
                        ["result"] = null,
                    }
                );
                continue;
            }

            if ((int?)message["id"] == id)
                return message;
        }

        throw new TimeoutException($"no response to request {id} within {timeout.TotalSeconds:0}s");
    }

    private void Write(JObject message)
    {
        var payload = Encoding.UTF8.GetBytes(message.ToString(Newtonsoft.Json.Formatting.None));
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        lock (_writeLock)
        {
            _stdin.Write(header);
            _stdin.Write(payload);
            _stdin.Flush();
        }
    }

    private JObject? ReadMessage(TimeSpan timeout)
    {
        // VSTHRD002: a bounded blocking read is exactly what a synchronous test client
        // wants; the wait is on a thread-pool task with no synchronization context, and it
        // gives up at the deadline rather than hanging the suite.
#pragma warning disable VSTHRD002
        var read = Task.Run(ReadMessageBlocking);
        return read.Wait(timeout) ? read.Result : null;
#pragma warning restore VSTHRD002
    }

    private JObject? ReadMessageBlocking()
    {
        var contentLength = 0;
        while (true)
        {
            var line = ReadLine();
            if (line is null)
                return null;
            if (line.Length == 0)
                break;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line["Content-Length:".Length..].Trim());
        }

        var buffer = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var got = _stdout.Read(buffer, offset, contentLength - offset);
            if (got <= 0)
                return null;
            offset += got;
        }

        return JObject.Parse(Encoding.UTF8.GetString(buffer));
    }

    private string? ReadLine()
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = _stdout.ReadByte();
            if (b < 0)
                return null;
            if (b == '\n')
                return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            bytes.Add((byte)b);
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: the server may already be gone.
        }

        _process.Dispose();
    }
}
