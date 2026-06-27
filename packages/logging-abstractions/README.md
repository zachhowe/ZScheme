# ZScheme Logging Abstractions

A thin ZScheme wrapper over **Microsoft.Extensions.Logging.Abstractions** — the
provider-agnostic logging surface. Use it wherever you already have an `ILogger` or an
`ILoggerFactory` (from DI, from a host, or from the companion `logging` package) and want
to emit structured logs.

For creating a standalone `ILoggerFactory` and configuring providers, see the `logging`
package, which builds on this one.

## Installation

```scheme
(dependencies
  (zscheme
    [logging-abstractions :local "../logging-abstractions"]))
```

## Import

```scheme
(import logging-abstractions/core)
```

## API Reference

### Logger acquisition

| Function | Signature | Description |
|---|---|---|
| `logger/from-factory` | `(ILoggerFactory String -> ILogger)` | A category-named logger from a factory |
| `null-logger` | `ILogger` | A shared no-op logger that discards everything (handy as a default/sink) |
| `logger/enabled?` | `(ILogger LogLevel -> Bool)` | Whether the logger would emit at the level (guard costly messages) |

### Log verbs

`log/trace`, `log/debug`, `log/info`, `log/warning`, `log/error`, `log/critical` —
each `(ILogger String args... -> Unit)`. The `String` is a message template and the
trailing `args` fill its `{Placeholder}` holes (structured logging), e.g.

```scheme
(log/info logger "user {Id} hit {Path}" id path)
```

### LogLevel values

`log-level/trace`, `log-level/debug`, `log-level/information`, `log-level/warning`,
`log-level/error`, `log-level/critical`, `log-level/none` — the
`Microsoft.Extensions.Logging.LogLevel` enum members, for use with `logger/enabled?`
(and `logging-builder/set-minimum-level` in the `logging` package).

## Usage

```scheme
(import logging-abstractions/core)

(define (greet [logger : Microsoft.Extensions.Logging.ILogger] [name : String]) : Unit
  (begin
    (if (logger/enabled? logger log-level/debug)
        (log/debug logger "preparing greeting for {Name}" name)
        ())
    (log/info logger "hello {Name}" name)))
```

## Dependencies

- **ZScheme** — `stdlib`
- **NuGet** — `Microsoft.Extensions.Logging.Abstractions`
