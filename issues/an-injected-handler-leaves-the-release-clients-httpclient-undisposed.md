# An injected handler leaves the release client's `HttpClient` undisposed

**Found by:** code review of the `version-selector` branch (the zsup toolchain
manager). Traced from source; no live repro was attempted.

**Affects:** `GitHubReleaseClient.Dispose`
(`src/ZScheme.Toolchain/GitHubReleaseClient.cs:328-332`), in combination with the
constructor at `:89-90`.

## Symptom

None today — every production caller constructs the client without a handler
(`InstallCommand.cs:148`, and `SelfCommand`'s update path), so `_ownsClient` is
true and the wrapper is disposed. This is a latent leak that fires the moment
production code passes a shared `HttpMessageHandler`, which is the standard way to
share a connection pool and exactly what the parameter is there to allow.

## Root cause

One flag governs two decisions that are not the same decision:

```csharp
_ownsClient = handler is null;
_http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
...
public void Dispose()
{
    if (_ownsClient)
        _http.Dispose();
}
```

The `disposeHandler: false` is correct — an injected handler belongs to the
caller. But the `HttpClient` *wrapper* is constructed here in both branches and is
owned here in both branches; skipping its `Dispose` leaks whatever the wrapper
itself holds, including its own cancellation registrations and the timer behind
`Timeout`.

The distinction the code needs is "who owns the handler", and it is already
expressed by the `disposeHandler` argument. `_ownsClient` restates it and then
applies it to the wrong object.

## Suggested fix direction

Always dispose the client, and let `disposeHandler` carry the ownership question
it was designed for:

```csharp
_http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

public void Dispose()
{
    // Always: the wrapper is constructed here either way. Whether the *handler* is disposed
    // with it is the disposeHandler argument above, which is the only ownership question.
    _http.Dispose();
}
```

`_ownsClient` then has no readers and can go.

Worth a test asserting an injected handler survives the client's disposal — the
existing handler-injection fixtures in the release-client tests already have
everything needed to observe it.
