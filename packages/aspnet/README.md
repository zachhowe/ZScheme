# zscheme-aspnet

A minimalist ZScheme wrapper around ASP.NET Core: routing, middleware, and request/response accessors.

## Status

Early — single-file routes, request/response basics, and `Use`-style middleware. JSON serialization lives in `stdlib/json`.

## Implementation

The modules bind directly to ASP.NET Core via `import-clr` — there is no C# bridge
assembly. The compiler's signature-directed overload resolution and delegate coercion
select the `RequestDelegate`/`Func<HttpContext, Func<Task>, Task>` overloads of
`MapGet`/`Use` and coerce ZScheme handlers into them, and the `:from "Assembly"`
import-clr hint resolves the extension-method types whose namespace differs from their
assembly (e.g. `EndpointRouteBuilderExtensions` in `Microsoft.AspNetCore.Routing.dll`).

## Sketch

```scheme
(import aspnet/app)
(import aspnet/router)
(import aspnet/request)
(import aspnet/response)
(import aspnet/middleware)

(define-async (hello [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (response/write-string ctx "hello world"))

(define (main) : Unit
  (let ([builder (app/create-builder)])
    (let ([application (app/build builder)])
      (begin
        (route/get application "/hello" hello)
        (app/run application)))))
```

## Modules

| Module | Purpose |
|---|---|
| `app` | `WebApplication` lifecycle: create-builder, build, run, start, shutdown. Token-accepting variants `app/start-with-token`, `app/run-async-with-token`, `app/shutdown-with-token` take a `CancellationToken` (see `stdlib/concurrent/cancellation`) |
| `router` | `route/get`, `route/post`, `route/put`, `route/patch`, `route/delete` |
| `request` | Method, path, route values, query (string + typed `query-int`), headers, body |
| `response` | Status, headers, body writers (string + JSON) |
| `middleware` | `app/use` for the request pipeline |
| `auth` | Bearer-token and Basic-auth gate middleware factories |
| `services` | ASP.NET accessors onto the DI container: `services/builder-services` (the `IServiceCollection` to register on before build) and `services/app-services` (the root provider). The provider-agnostic registration/resolution verbs (`services/add-singleton[-self/-instance/-factory]`, `-scoped` / `-transient`, `services/get-required-service` / `-service`) come from the standalone `di-abstractions/services` package, keyed by `(typeof T)`; resolve from the request's scoped provider (`request/services`) |
| `logging` | ASP.NET glue over the `logging-abstractions` / `logging` packages: `logging/request-logger` / `logging/app-logger` to obtain a category logger, and `logging/clear-providers` to silence a builder. The `log/trace…log/critical` verbs come from `logging-abstractions/core` |

## Logging

The provider-agnostic logging surface (the `log/*` verbs, `LogLevel`, `ILogger`
acquisition) lives in the standalone `logging-abstractions` package, and `ILoggingBuilder`
configuration lives in the `logging` package. This module only adds the ASP.NET-typed glue.

`app/create-builder` keeps the framework's default logging providers, so apps log to the
console out of the box. Obtain a category-named `ILogger` from the request-scoped provider
with `logging/request-logger`, and emit with the variadic `log/*` verbs (imported from
`logging-abstractions/core`), which accept a message template plus structured arguments:

```scheme
(import aspnet/logging)
(import logging-abstractions/core)

(define-async (handle [ctx : Microsoft.AspNetCore.Http.HttpContext]) : Task
  (let ([logger (logging/request-logger ctx "MyApp")])
    (begin
      (log/info logger "{Method} {Path}" (request/method ctx) (request/path ctx))
      (await (response/write-string ctx "ok")))))
```

Tests and quiet apps can remove all providers with `logging/clear-providers`:

```scheme
(let ([builder (logging/clear-providers (app/create-builder))]) ...)
```

A runnable example lives in `examples/aspnet-hello/` — a small Exe package (routing,
middleware, request/response, and a DI-registered `Greeter` resolved per request) that
depends on this package, `di-abstractions`, and `logging-abstractions`.
