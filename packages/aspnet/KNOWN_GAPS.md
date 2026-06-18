# Known gaps

Tracked limitations of the `zscheme-aspnet` package as of v0.1.0. Resolve and remove entries as the underlying issues are fixed.

## Why the bridge exists at all

`MapGet` / `Use` have multiple `Delegate`-typed overloads in different static classes. The bridge originally existed because ZScheme could not (a) pick the `RequestDelegate` overload over `Delegate` based on declared signature, nor (b) coerce a named handler function into a `RequestDelegate`.

Both compiler features now exist and are exercised end-to-end (on both the C# and IL backends) by `AspNetInteropTests`, which binds directly to `Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions/MapGet`:
- **signature-directed overload resolution** — `import-clr` with a `(delegate TypeName)`-annotated parameter resolves to the concrete-delegate overload (`RequestDelegate`) over the abstract `System.Delegate` overload (`ClrInterop.ResolveOverloadCallSite` + `FuncTypeMatchesDelegate`).
- **automatic delegate-shape conversion** — a named function passed where a delegate is expected is coerced: the C# backend emits an adapter-lambda cast to the delegate type; the IL backend constructs the delegate via `newobj` against the resolved overload's parameter type.

The bridge is intentionally **retained** for now: it keeps the public package surface stable and avoids a remaining sharp edge — `ClrInterop` resolves CLR types by matching the type's namespace prefix against assembly file names, so types whose namespace differs from their assembly (e.g. `Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions`, which ships in `Microsoft.AspNetCore.Routing.dll`) are not found unless that assembly is already loaded. Resolving that probing limitation is the prerequisite for replacing the bridge methods with direct `import-clr` bindings.

## Missing surface

- No `CancellationToken` is threaded through `app/run` / `app/run-async` (a host can be started via `app/start` and stopped via `app/shutdown`, but callers can't pass their own cancellation token).
- No DI / service registration hooks on `WebApplicationBuilder.Services`.
- No structured logging hookup — `WebAppBridge.CreateBuilder` calls `Logging.ClearProviders()`, and middleware can write headers but not log via `ILogger`.
