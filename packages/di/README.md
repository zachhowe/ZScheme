# ZScheme DI

A ZScheme wrapper over **Microsoft.Extensions.DependencyInjection** — the concrete layer
that turns a registered service collection into a live `IServiceProvider`. It builds on
`di-abstractions` (which provides the `service-collection/new` constructor, the
registration verbs, and resolution).

## Installation

```scheme
(dependencies
  (zscheme
    [di :local "../di"]
    [di-abstractions :local "../di-abstractions"]))
```

## Import

```scheme
(import di/provider)
(import di-abstractions/services)   ;; for registration and resolution
```

## API Reference

| Function | Signature | Description |
|---|---|---|
| `services/build-provider` | `(IServiceCollection -> IServiceProvider)` | Build a live provider from a registered collection |

Register services with the `di-abstractions` verbs, build the provider here, then resolve
with `di-abstractions`' `services/get-required-service` / `services/get-service`.

## Usage

```scheme
(import di/provider)
(import di-abstractions/services)

(define-record Greeter [prefix : String])

(define (make-greeter) : Greeter
  (let ([svcs (service-collection/new)])
    (services/add-singleton-instance svcs (typeof Greeter) (Greeter "hello"))
    (let ([provider (services/build-provider svcs)])
      (services/get-required-service provider))))
```

## Dependencies

- **ZScheme** — `di-abstractions`
- **NuGet** — `Microsoft.Extensions.DependencyInjection`
