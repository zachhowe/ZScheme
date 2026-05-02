# zscheme-aspnet

A minimalist ZScheme wrapper around ASP.NET Core: routing, middleware, JSON helpers, and request/response accessors.

## Status

Early — single-file routes, request/response basics, and `Use`-style middleware. JSON helpers are string-based.

## Why a bridge assembly?

ASP.NET Core's `MapGet`/`Use` overloads use `RequestDelegate` and `Delegate`, which ZScheme's `import-clr` cannot disambiguate or auto-convert from `Func<HttpContext, Task>`. The `bridge/` C# project re-exports the surface with unambiguous signatures that take `Func<HttpContext, Task>` directly, which is exactly what ZScheme produces from `(define-async (handler [ctx : HttpContext]) : Task ...)`.

The bridge must be built before the package is consumed:

```bash
dotnet build packages/aspnet/bridge -c Release
```

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
| `app` | `WebApplication` lifecycle: create-builder, build, run |
| `router` | `route/get`, `route/post`, `route/put`, `route/patch`, `route/delete` |
| `request` | Method, path, route values, query, headers, body |
| `response` | Status, headers, body writers (string + JSON) |
| `middleware` | `app/use` for the request pipeline |
| `auth` | Bearer / basic-auth gate middleware factories |
| `json` | `System.Text.Json` string-based serialize / deserialize |
