;; services.zs — dependency injection over Microsoft.Extensions.DependencyInjection.Abstractions:
;; build a service collection, register services on it, and resolve them from a provider.
;;
;; The registration verbs (AddSingleton/AddScoped/AddTransient) and resolution verbs
;; (GetRequiredService/GetService) are extension methods in the Abstractions assembly; they
;; bind as static methods on their declaring extension class with the receiver as the first
;; explicit parameter. Service keys are System.Type values produced by `typeof`
;; (e.g. (typeof MyRecord)); resolution is generic, instantiated from the expected return
;; type at the call site, the same way stdlib/json's Deserialize<T> works.
;;
;; This is the provider-agnostic surface: it operates on an IServiceCollection (built here
;; with `service-collection/new`, or supplied by a host such as ASP.NET) and any
;; IServiceProvider. Turning a collection into a live provider needs the concrete
;; `di` package's `services/build-provider`.
(module services)

(import-clr
  Microsoft.Extensions.DependencyInjection

  ;; --- Registration: SINGLETON (type->type, self, instance, factory) ---
  [clr-add-singleton-type Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions/AddSingleton
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (Microsoft.Extensions.DependencyInjection.IServiceCollection System.Type System.Type
       -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]
  [clr-add-singleton-self Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions/AddSingleton
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (Microsoft.Extensions.DependencyInjection.IServiceCollection System.Type
       -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]
  ;; Instance registration exists for Singleton only (Scoped/Transient have no instance
  ;; overload — a per-scope/per-resolve lifetime is meaningless for a shared instance).
  [clr-add-singleton-instance Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions/AddSingleton
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (Microsoft.Extensions.DependencyInjection.IServiceCollection System.Type System.Object
       -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]
  [clr-add-singleton-factory Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions/AddSingleton
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (Microsoft.Extensions.DependencyInjection.IServiceCollection System.Type
       (System.IServiceProvider -> System.Object)
       -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]

  ;; --- Registration: SCOPED (type->type, self, factory) ---
  [clr-add-scoped-type Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions/AddScoped
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (Microsoft.Extensions.DependencyInjection.IServiceCollection System.Type System.Type
       -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]
  [clr-add-scoped-self Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions/AddScoped
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (Microsoft.Extensions.DependencyInjection.IServiceCollection System.Type
       -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]
  [clr-add-scoped-factory Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions/AddScoped
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (Microsoft.Extensions.DependencyInjection.IServiceCollection System.Type
       (System.IServiceProvider -> System.Object)
       -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]

  ;; --- Registration: TRANSIENT (type->type, self, factory) ---
  [clr-add-transient-type Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions/AddTransient
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (Microsoft.Extensions.DependencyInjection.IServiceCollection System.Type System.Type
       -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]
  [clr-add-transient-self Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions/AddTransient
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (Microsoft.Extensions.DependencyInjection.IServiceCollection System.Type
       -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]
  [clr-add-transient-factory Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions/AddTransient
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (Microsoft.Extensions.DependencyInjection.IServiceCollection System.Type
       (System.IServiceProvider -> System.Object)
       -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]

  ;; --- Resolution (generic; T derived from the expected return type) ---
  ;; GetRequiredService<T> throws when T is unregistered; GetService<T> returns null.
  ;; Exported directly (not wrapped in a define) so the ^a return reaches the call site,
  ;; mirroring stdlib/json's json/deserialize.
  [services/get-required-service Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions/GetRequiredService ^a
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (System.IServiceProvider -> ^a)]
  [services/get-service Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions/GetService ^a
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (System.IServiceProvider -> ^a)]

  ;; --- Scopes (one resolution context for scoped lifetimes) ---
  ;; CreateScope yields an IServiceScope; resolve scoped services from its ServiceProvider.
  [clr-create-scope Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions/CreateScope
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (System.IServiceProvider -> Microsoft.Extensions.DependencyInjection.IServiceScope)]
  [scope-service-provider Microsoft.Extensions.DependencyInjection.IServiceScope.ServiceProvider
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    :instance-property : (Microsoft.Extensions.DependencyInjection.IServiceScope
                          -> System.IServiceProvider)])

;; --- Service collection ---

;; A fresh, empty service collection to register services on (then build a provider with
;; `di`'s services/build-provider, or hand to a host).
(define (service-collection/new)
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (new Microsoft.Extensions.DependencyInjection.ServiceCollection))

;; --- Singleton registration ---

;; Register `impl-type` as the singleton implementation of `service-type`. Note: the DI
;; container activates `impl-type` via a constructor whose parameters are themselves
;; resolvable services; plain ZScheme records/classes (all-fields ctor) are usually
;; better registered with -instance or -factory below.
(define (services/add-singleton
          [svcs : Microsoft.Extensions.DependencyInjection.IServiceCollection]
          [service-type : System.Type] [impl-type : System.Type])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (clr-add-singleton-type svcs service-type impl-type))

;; Register `service-type` as its own singleton implementation.
(define (services/add-singleton-self
          [svcs : Microsoft.Extensions.DependencyInjection.IServiceCollection]
          [service-type : System.Type])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (clr-add-singleton-self svcs service-type))

;; Register a pre-built `instance` as the singleton for `service-type`.
(define (services/add-singleton-instance
          [svcs : Microsoft.Extensions.DependencyInjection.IServiceCollection]
          [service-type : System.Type] [instance : System.Object])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (clr-add-singleton-instance svcs service-type instance))

;; Register a `factory` that builds the singleton for `service-type` on first resolve.
(define (services/add-singleton-factory
          [svcs : Microsoft.Extensions.DependencyInjection.IServiceCollection]
          [service-type : System.Type]
          [factory : (System.IServiceProvider -> System.Object)])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (clr-add-singleton-factory svcs service-type factory))

;; --- Scoped registration (one instance per request scope) ---

(define (services/add-scoped
          [svcs : Microsoft.Extensions.DependencyInjection.IServiceCollection]
          [service-type : System.Type] [impl-type : System.Type])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (clr-add-scoped-type svcs service-type impl-type))

(define (services/add-scoped-self
          [svcs : Microsoft.Extensions.DependencyInjection.IServiceCollection]
          [service-type : System.Type])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (clr-add-scoped-self svcs service-type))

(define (services/add-scoped-factory
          [svcs : Microsoft.Extensions.DependencyInjection.IServiceCollection]
          [service-type : System.Type]
          [factory : (System.IServiceProvider -> System.Object)])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (clr-add-scoped-factory svcs service-type factory))

;; --- Transient registration (a new instance on every resolve) ---

(define (services/add-transient
          [svcs : Microsoft.Extensions.DependencyInjection.IServiceCollection]
          [service-type : System.Type] [impl-type : System.Type])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (clr-add-transient-type svcs service-type impl-type))

(define (services/add-transient-self
          [svcs : Microsoft.Extensions.DependencyInjection.IServiceCollection]
          [service-type : System.Type])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (clr-add-transient-self svcs service-type))

(define (services/add-transient-factory
          [svcs : Microsoft.Extensions.DependencyInjection.IServiceCollection]
          [service-type : System.Type]
          [factory : (System.IServiceProvider -> System.Object)])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (clr-add-transient-factory svcs service-type factory))

;; --- Scopes ---

;; Open a new scope on `provider`; resolve scoped services from `scope/services` of the
;; returned scope. The scope is IDisposable — dispose it (e.g. via `use`) to release
;; scoped instances.
(define (service-provider/create-scope [provider : System.IServiceProvider])
  : Microsoft.Extensions.DependencyInjection.IServiceScope
  (clr-create-scope provider))

;; The IServiceProvider backing a scope — resolve scoped/transient services from here.
(define (scope/services [scope : Microsoft.Extensions.DependencyInjection.IServiceScope])
  : System.IServiceProvider
  (scope-service-provider scope))

(export service-collection/new
        services/add-singleton services/add-singleton-self
        services/add-singleton-instance services/add-singleton-factory
        services/add-scoped services/add-scoped-self services/add-scoped-factory
        services/add-transient services/add-transient-self services/add-transient-factory
        services/get-required-service services/get-service
        service-provider/create-scope scope/services)
