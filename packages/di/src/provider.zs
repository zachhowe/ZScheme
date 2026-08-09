;; provider.zs — service-provider construction over Microsoft.Extensions.DependencyInjection
;; (the concrete layer above the Abstractions package).
;;
;; `services/build-provider` is the bridge from a registered IServiceCollection to a live
;; IServiceProvider you can resolve from — the concrete container that BuildServiceProvider
;; in Microsoft.Extensions.DependencyInjection.dll constructs. Register services and resolve
;; them with the `di-abstractions` verbs; this module just produces the provider in between.
(module provider)

(import-clr
  Microsoft.Extensions.DependencyInjection

  ;; ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(IServiceCollection)
  ;; : ServiceProvider — the concrete provider, returned here as System.IServiceProvider so
  ;; it feeds di-abstractions' get-required-service/get-service directly. Lives in
  ;; Microsoft.Extensions.DependencyInjection.dll (NOT Abstractions), so name it via :from.
  [clr-build-provider ServiceCollectionContainerBuilderExtensions/BuildServiceProvider
    :from "Microsoft.Extensions.DependencyInjection"
    : (IServiceCollection -> System.IServiceProvider)])

;; --- Provider construction ---

;; Build a live IServiceProvider from a registered collection. Resolve services from the
;; result with di-abstractions' services/get-required-service / services/get-service.
(define (services/build-provider [svcs : IServiceCollection])
  : System.IServiceProvider
  (clr-build-provider svcs))

(export services/build-provider)
