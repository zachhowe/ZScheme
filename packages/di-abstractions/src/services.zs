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

  ;; --- Service collection (the registration target) ---
  ;; IServiceCollection.Count : the number of registered descriptors (inherited from
  ;; ICollection<ServiceDescriptor>); handy for asserting registrations took effect.
  [collection-count IServiceCollection.Count
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    :instance-property : (IServiceCollection -> Int)]

  ;; --- Registration: SINGLETON (type->type, self, instance, factory) ---
  [clr-add-singleton-type ServiceCollectionServiceExtensions/AddSingleton
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (IServiceCollection System.Type System.Type
       -> IServiceCollection)]
  [clr-add-singleton-self ServiceCollectionServiceExtensions/AddSingleton
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (IServiceCollection System.Type -> IServiceCollection)]
  ;; Instance registration exists for Singleton only (Scoped/Transient have no instance
  ;; overload — a per-scope/per-resolve lifetime is meaningless for a shared instance).
  [clr-add-singleton-instance ServiceCollectionServiceExtensions/AddSingleton
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (IServiceCollection System.Type System.Object -> IServiceCollection)]
  [clr-add-singleton-factory ServiceCollectionServiceExtensions/AddSingleton
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (IServiceCollection System.Type (System.IServiceProvider -> System.Object) -> IServiceCollection)]

  ;; --- Registration: SCOPED (type->type, self, factory) ---
  [clr-add-scoped-type ServiceCollectionServiceExtensions/AddScoped
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (IServiceCollection System.Type System.Type -> IServiceCollection)]
  [clr-add-scoped-self ServiceCollectionServiceExtensions/AddScoped
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (IServiceCollection System.Type -> IServiceCollection)]
  [clr-add-scoped-factory ServiceCollectionServiceExtensions/AddScoped
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (IServiceCollection System.Type (System.IServiceProvider -> System.Object) -> IServiceCollection)]

  ;; --- Registration: TRANSIENT (type->type, self, factory) ---
  [clr-add-transient-type ServiceCollectionServiceExtensions/AddTransient
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (IServiceCollection System.Type System.Type -> IServiceCollection)]
  [clr-add-transient-self ServiceCollectionServiceExtensions/AddTransient
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (IServiceCollection System.Type -> IServiceCollection)]
  [clr-add-transient-factory ServiceCollectionServiceExtensions/AddTransient
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (IServiceCollection System.Type (System.IServiceProvider -> System.Object) -> IServiceCollection)]

  ;; --- Resolution (generic; T derived from the expected return type) ---
  ;; GetRequiredService<T> throws when T is unregistered; GetService<T> returns null.
  ;; Exported directly (not wrapped in a define) so the ^a return reaches the call site,
  ;; mirroring stdlib/json's json/deserialize.
  [services/get-required-service ServiceProviderServiceExtensions/GetRequiredService ^a
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (System.IServiceProvider -> ^a)]
  [services/get-service ServiceProviderServiceExtensions/GetService ^a
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (System.IServiceProvider -> ^a)]

  ;; --- Scopes (one resolution context for scoped lifetimes) ---
  ;; CreateScope yields an IServiceScope; resolve scoped services from its ServiceProvider.
  [clr-create-scope ServiceProviderServiceExtensions/CreateScope
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    : (System.IServiceProvider -> IServiceScope)]
  [scope-service-provider IServiceScope.ServiceProvider
    :from "Microsoft.Extensions.DependencyInjection.Abstractions"
    :instance-property : (IServiceScope -> System.IServiceProvider)])

;; --- Service collection ---

;; A fresh, empty service collection to register services on (then build a provider with
;; `di`'s services/build-provider, or hand to a host).
(define (service-collection/new)
  : IServiceCollection
  (new ServiceCollection))

;; The number of registered service descriptors.
(define (service-collection/count
          [svcs : IServiceCollection]) : Int
  (collection-count svcs))

;; --- Singleton registration ---

;; Register `impl-type` as the singleton implementation of `service-type`. Note: the DI
;; container activates `impl-type` via a constructor whose parameters are themselves
;; resolvable services; plain ZScheme records/classes (all-fields ctor) are usually
;; better registered with -instance or -factory below.
(define (services/add-singleton
          [svcs : IServiceCollection]
          [service-type : System.Type] [impl-type : System.Type])
  : IServiceCollection
  (clr-add-singleton-type svcs service-type impl-type))

;; Register `service-type` as its own singleton implementation.
(define (services/add-singleton-self
          [svcs : IServiceCollection]
          [service-type : System.Type])
  : IServiceCollection
  (clr-add-singleton-self svcs service-type))

;; Register a pre-built `instance` as the singleton for `service-type`.
(define (services/add-singleton-instance
          [svcs : IServiceCollection]
          [service-type : System.Type] [instance : System.Object])
  : IServiceCollection
  (clr-add-singleton-instance svcs service-type instance))

;; Register a `factory` that builds the singleton for `service-type` on first resolve.
(define (services/add-singleton-factory
          [svcs : IServiceCollection]
          [service-type : System.Type]
          [factory : (System.IServiceProvider -> System.Object)])
  : IServiceCollection
  (clr-add-singleton-factory svcs service-type factory))

;; --- Scoped registration (one instance per request scope) ---

(define (services/add-scoped
          [svcs : IServiceCollection]
          [service-type : System.Type] [impl-type : System.Type])
  : IServiceCollection
  (clr-add-scoped-type svcs service-type impl-type))

(define (services/add-scoped-self
          [svcs : IServiceCollection]
          [service-type : System.Type])
  : IServiceCollection
  (clr-add-scoped-self svcs service-type))

(define (services/add-scoped-factory
          [svcs : IServiceCollection]
          [service-type : System.Type]
          [factory : (System.IServiceProvider -> System.Object)])
  : IServiceCollection
  (clr-add-scoped-factory svcs service-type factory))

;; --- Transient registration (a new instance on every resolve) ---

(define (services/add-transient
          [svcs : IServiceCollection]
          [service-type : System.Type] [impl-type : System.Type])
  : IServiceCollection
  (clr-add-transient-type svcs service-type impl-type))

(define (services/add-transient-self
          [svcs : IServiceCollection]
          [service-type : System.Type])
  : IServiceCollection
  (clr-add-transient-self svcs service-type))

(define (services/add-transient-factory
          [svcs : IServiceCollection]
          [service-type : System.Type]
          [factory : (System.IServiceProvider -> System.Object)])
  : IServiceCollection
  (clr-add-transient-factory svcs service-type factory))

;; --- Scopes ---

;; Open a new scope on `provider`; resolve scoped services from `scope/services` of the
;; returned scope. The scope is IDisposable — dispose it (e.g. via `use`) to release
;; scoped instances.
(define (service-provider/create-scope [provider : System.IServiceProvider])
  : IServiceScope
  (clr-create-scope provider))

;; The IServiceProvider backing a scope — resolve scoped/transient services from here.
(define (scope/services [scope : IServiceScope])
  : System.IServiceProvider
  (scope-service-provider scope))

(export service-collection/new service-collection/count
        services/add-singleton services/add-singleton-self
        services/add-singleton-instance services/add-singleton-factory
        services/add-scoped services/add-scoped-self services/add-scoped-factory
        services/add-transient services/add-transient-self services/add-transient-factory
        services/get-required-service services/get-service
        service-provider/create-scope scope/services)
