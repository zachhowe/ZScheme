# Known gaps

Tracked limitations of the `zscheme-aspnet` package as of v0.1.0. Resolve and remove entries as the underlying issues are fixed.

## No bridge

The package once carried a hand-written C# bridge (`bridge/`) that re-exported the
ASP.NET surface with unambiguous signatures, because ZScheme could not (a) pick the
`RequestDelegate` overload of `MapGet`/`Use` over `Delegate`, nor (b) coerce a handler
into a `RequestDelegate`. The bridge has been **removed**; the modules bind directly to
the framework. What replaced it:

- **signature-directed overload resolution** — selects the concrete-delegate overload
  (`RequestDelegate`, or `Func<HttpContext, Func<Task>, Task>` for `Use`) using the
  declared/inferred function shape (`ClrInterop.ResolveOverloadCallSite`,
  `Unifier` arity-aware delegate↔func matching).
- **automatic delegate-shape conversion** — a ZScheme handler passed where a delegate is
  expected is coerced: the C# backend emits an adapter-lambda cast; the IL backend
  constructs the delegate via `newobj` (over the function's static method, or over a
  closure value's `Invoke`).
- **`:from "Assembly"` on `import-clr`** — loads the named assembly so types whose
  namespace differs from their assembly file (e.g.
  `Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions` in
  `Microsoft.AspNetCore.Routing.dll`) resolve without any pre-loading hack.

`AspNetInteropTests` exercises the direct `MapGet` binding end-to-end on both backends.

## Missing surface

- No `CancellationToken` is threaded through `app/run` / `app/run-async` (a host can be started via `app/start` and stopped via `app/shutdown`, but callers can't pass their own cancellation token).
- No DI / service registration hooks on `WebApplicationBuilder.Services`.
- No structured logging hookup — `app/create-builder` calls `Logging.ClearProviders()`, and middleware can write headers but not log via `ILogger`.
