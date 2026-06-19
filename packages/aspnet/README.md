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
  (let [builder (app/create-builder)]
    (let [application (app/build builder)]
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
