# Known gaps

Tracked limitations of the `zscheme-aspnet` package as of v0.1.0. Resolve and remove entries as the underlying issues are fixed.

## Typed JSON serialization

`json/serialize-typed` requires the caller to pass a `System.Type` argument:

```scheme
(json/serialize-typed value (typeof MyRecord))
```

The ergonomic generic form `(json/serialize value)` is not yet available because ZScheme's `import-clr` overload selection picks one of `JsonSerializer.Serialize`'s many generic overloads non-deterministically, and there is no built-in way to bind to a specific generic instantiation per call site. Revisit once ZScheme generic-method binding improves.

## Bridge must be pre-built

The package depends on `bridge/bin/Release/net10.0/ZScheme.AspNet.Bridge.dll`. `run-package-tests.ps1` builds it automatically, but consumers outside that script must run:

```bash
dotnet build packages/aspnet/bridge -c Release
```

before installing or consuming the package. A future improvement would be to teach `zs install` to detect a bridge subproject and build it as part of installation.

## Why the bridge exists at all

`MapGet` / `Use` have multiple `Delegate`-typed overloads in different static classes. ZScheme's `import-clr` resolves overloads by name + parameter count using `MethodInfo` heuristics — it cannot pick the `RequestDelegate` overload over `Delegate` based on declared signature, and `Func<HttpContext, Task>` (what ZScheme produces from `(define-async (handler [ctx : HttpContext]) : Task ...)`) is not implicitly convertible to `RequestDelegate` at the IL level. The bridge sidesteps both problems by exposing one method per route verb, each accepting `Func<HttpContext, Task>` directly.

If ZScheme gains:
- signature-directed overload resolution in `import-clr`, AND
- automatic delegate-shape conversion (or a `(delegate TypeName fn)` form),

then most bridge methods could be replaced with direct `import-clr` bindings to ASP.NET Core extension methods.

## Codegen tests for the Unit-typed CLR call fix

`CSharpEmitter.Emit.cs::EmitLetStmt` was patched to emit `expr;` instead of `_ = expr;` when the value is an `IrNode.ClrCall` with `Type == ZType.Unit` (avoiding CS8209 for `void` C# methods). The fix is currently regression-tested only end-to-end via `examples/aspnet-hello`. A targeted IR-level unit test under `tests/ZScheme.Compiler.Tests/Codegen/` would be more durable.

## Integration tests are smoke-only

`packages/aspnet/test/aspnet-tests.zs` only checks that bindings resolve and that `auth/require-bearer` returns a function. There is no end-to-end test that boots a `WebApplication` on a random port, sends real requests, and asserts responses. The example app under `examples/aspnet-hello/` is the de facto integration check today; promoting that flow into an automated test (likely needs an `app/url-add` + `app/run-async` orchestration helper) would be a real win.

## Missing surface

- No typed query / route parameter parsing helpers (callers manually convert `request/query` strings).
- No `auth/require-basic` middleware factory yet (only bearer is implemented).
- No graceful shutdown / cancellation token threading exposed.
- No DI / service registration hooks on `WebApplicationBuilder.Services`.
- No structured logging hookup — middleware can write headers but not log via `ILogger`.
