# Known gaps

Tracked limitations of the `zscheme-aspnet` package as of v0.1.0. Resolve and remove entries as the underlying issues are fixed.

## Why the bridge exists at all

`MapGet` / `Use` have multiple `Delegate`-typed overloads in different static classes. ZScheme's `import-clr` resolves overloads by name + parameter count using `MethodInfo` heuristics — it cannot pick the `RequestDelegate` overload over `Delegate` based on declared signature, and `Func<HttpContext, Task>` (what ZScheme produces from `(define-async (handler [ctx : HttpContext]) : Task ...)`) is not implicitly convertible to `RequestDelegate` at the IL level. The bridge sidesteps both problems by exposing one method per route verb, each accepting `Func<HttpContext, Task>` directly.

If ZScheme gains:
- signature-directed overload resolution in `import-clr`, AND
- automatic delegate-shape conversion (or a `(delegate TypeName fn)` form),

then most bridge methods could be replaced with direct `import-clr` bindings to ASP.NET Core extension methods.

## Missing surface

- No `CancellationToken` is threaded through `app/run` / `app/run-async` (a host can be started via `app/start` and stopped via `app/shutdown`, but callers can't pass their own cancellation token).
- No DI / service registration hooks on `WebApplicationBuilder.Services`.
- No structured logging hookup — `WebAppBridge.CreateBuilder` calls `Logging.ClearProviders()`, and middleware can write headers but not log via `ILogger`.
