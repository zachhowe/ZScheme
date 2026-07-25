# A didOpen that races the initialize handshake is silently dropped

**Found by:** investigating the `StdioServerTests` 90-second failure (fixed separately in
`aa457e2` — that turned out to be a URI-casing comparison, not this). This was **reproduced**
against a real `zs-lsp` process by pipelining initialize / initialized / didOpen without waiting
for the initialize response.

**Affects:** any LSP client that pipelines messages instead of waiting for the initialize
response. Not the in-repo tests: `StdioLspClient.Initialize`
(`tests/ZScheme.LanguageServer.Tests/TestFixtures/StdioLspClient.cs:61-75`) awaits the response
before returning, so the suite does not hit it.

## Symptom

The document is never analysed. No diagnostics are ever published for it, `GetDocument` keeps
returning null, and every navigation request answers "no result" instantly, forever. The only
trace is a line on **stderr**:

```
Unexpected notification textDocument/didOpen
```

which a client that does not drain stderr will never see.

## Root cause

Not ZScheme code — OmniSharp's `LspServerReceiver` refuses to route notifications until the
initialize/initialized handshake has completed, and drops them rather than queueing. Because
`didOpen` is a notification there is no response for the client to notice missing.

This is the same *shape* of failure `c2756b4` ("Never let one document's analysis fail silently")
set out to eliminate for the analysis path: a document that silently never exists. `c2756b4`
hardened `AnalysisService` against a throwing or hanging analysis, but a `didOpen` that never
reaches the handler at all is outside what it guards.

## Suggested fix direction

Two independent pieces:

1. **Make it visible.** The drop currently only reaches stderr. If OmniSharp exposes a hook for
   unroutable notifications, log it through the server's own Serilog sink (see
   `src/ZScheme.LanguageServer/StderrLogging.cs`) so it lands wherever the operator is already
   looking. Failing that, note the hazard in the server's startup log.
2. **Make the test client honest about it.** `StdioLspClient` currently avoids the race by
   construction. A test that deliberately pipelines and asserts the *documented* behaviour would
   pin whether a future OmniSharp bump starts queueing instead of dropping — worth having, since
   the library is pinned at 0.19.9 and this is behaviour we inherit rather than own.

Do not attempt to buffer notifications inside ZScheme's handlers; the receiver drops them before
any handler runs, so there is nothing to intercept at that layer.

## Priority note

Low for the suite, potentially high for a real editor integration, and it depends entirely on
client behaviour. Recorded because the failure is completely invisible from the client side —
which is the specific quality `c2756b4` was trying to stamp out — and because whoever hits it
will otherwise spend a long time looking at ZScheme code for a fault that is not there.
