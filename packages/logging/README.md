# ZScheme Logging

A ZScheme wrapper over **Microsoft.Extensions.Logging** — the concrete layer that creates
logger factories and configures providers. It builds on `logging-abstractions` (which
provides `ILogger` acquisition, the `log/*` verbs, and `LogLevel`).

## Installation

```scheme
(dependencies
  (zscheme
    [logging :local "../logging"]
    [logging-abstractions :local "../logging-abstractions"]))
```

## Import

```scheme
(import logging/builder)
(import logging-abstractions/core)   ;; for logger/from-factory and the log/* verbs
```

## API Reference

| Function | Signature | Description |
|---|---|---|
| `logging/create-factory` | `((ILoggingBuilder -> Unit) -> ILoggerFactory)` | Build a standalone factory; the callback configures providers/levels |
| `logging-builder/clear-providers` | `(ILoggingBuilder -> ILoggingBuilder)` | Remove all registered providers (no output) |
| `logging-builder/set-minimum-level` | `(ILoggingBuilder LogLevel -> ILoggingBuilder)` | Drop messages below the given level |

The builder verbs return the builder so they can be chained inside the `create-factory`
callback (or run against any host's `ILoggingBuilder`, e.g. ASP.NET's
`WebApplicationBuilder.Logging`).

## Usage

```scheme
(import logging/builder)
(import logging-abstractions/core)

(define (configure [b : Microsoft.Extensions.Logging.ILoggingBuilder]) : Unit
  (begin
    (logging-builder/clear-providers b)
    (logging-builder/set-minimum-level b log-level/warning)
    ()))

(define (make-logger) : Microsoft.Extensions.Logging.ILogger
  (let ([factory (logging/create-factory configure)])
    (logger/from-factory factory "MyApp")))
```

## Dependencies

- **ZScheme** — `logging-abstractions`
- **NuGet** — `Microsoft.Extensions.Logging`
