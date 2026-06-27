# ZScheme DI Abstractions

A thin ZScheme wrapper over **Microsoft.Extensions.DependencyInjection.Abstractions** — the
provider-agnostic dependency-injection surface. Use it to build a service collection,
register services on it, and resolve them from any `IServiceProvider` (from DI, from a host
such as ASP.NET, or from the companion `di` package).

For turning a collection into a live `IServiceProvider`, see the `di` package, which builds
on this one with `services/build-provider`.

## Installation

```scheme
(dependencies
  (zscheme
    [di-abstractions :local "../di-abstractions"]))
```

## Import

```scheme
(import di-abstractions/services)
```

## API Reference

### Service collection

| Function | Signature | Description |
|---|---|---|
| `service-collection/new` | `(-> IServiceCollection)` | A fresh, empty collection to register on |

### Registration

Service keys are `System.Type` values from `typeof`. Each verb returns the collection.

| Function | Signature |
|---|---|
| `services/add-singleton` | `(IServiceCollection Type Type -> IServiceCollection)` |
| `services/add-singleton-self` | `(IServiceCollection Type -> IServiceCollection)` |
| `services/add-singleton-instance` | `(IServiceCollection Type Object -> IServiceCollection)` |
| `services/add-singleton-factory` | `(IServiceCollection Type (IServiceProvider -> Object) -> IServiceCollection)` |
| `services/add-scoped`, `-self`, `-factory` | as singleton, minus the instance overload |
| `services/add-transient`, `-self`, `-factory` | as singleton, minus the instance overload |

Singleton has an `-instance` overload (register a pre-built value); scoped and transient do
not, since a shared instance has no per-scope/per-resolve meaning.

### Resolution

| Function | Signature | Description |
|---|---|---|
| `services/get-required-service` | `(IServiceProvider -> ^a)` | Resolve `T`; throws if unregistered |
| `services/get-service` | `(IServiceProvider -> ^a)` | Resolve `T`; null if unregistered |

`T` is instantiated from the expected return type at the call site — annotate the binding:

```scheme
(let ([g : Greeter (services/get-required-service provider)]) ...)
```

### Scopes

| Function | Signature | Description |
|---|---|---|
| `service-provider/create-scope` | `(IServiceProvider -> IServiceScope)` | Open a scope (IDisposable) |
| `scope/services` | `(IServiceScope -> IServiceProvider)` | The provider backing the scope |

## Usage

```scheme
(import di-abstractions/services)
(import di/provider)   ;; for services/build-provider

(define-record Greeter [prefix : String])

(define (resolve-greeter) : Greeter
  (let* ([svcs (service-collection/new)]
         [_ (services/add-singleton-instance svcs (typeof Greeter) (Greeter "hello"))]
         [provider (services/build-provider svcs)])
    (services/get-required-service provider)))
```

## Dependencies

- **ZScheme** — `stdlib`
- **NuGet** — `Microsoft.Extensions.DependencyInjection.Abstractions`
