;; services.zs — ASP.NET-specific accessors onto the DI container.
;;
;; The provider-agnostic registration and resolution verbs (AddSingleton/AddScoped/
;; AddTransient, GetRequiredService/GetService) live in the standalone `di-abstractions`
;; package; import `di-abstractions/services` at the call site for those. This module only
;; adds the bindings that genuinely need ASP.NET types: reaching the IServiceCollection on
;; a WebApplicationBuilder (to register before Build) and the root IServiceProvider on a
;; WebApplication (to resolve at startup).
(module services)

(import-clr
  Microsoft.AspNetCore.Builder

  ;; WebApplicationBuilder.Services : IServiceCollection — register before Build.
  [builder-services WebApplicationBuilder.Services
    :instance-property : (WebApplicationBuilder
                          -> Microsoft.Extensions.DependencyInjection.IServiceCollection)]
  ;; WebApplication.Services : IServiceProvider — the ROOT provider (singletons only;
  ;; a request handler must resolve scoped services from request/services instead).
  [app-services WebApplication.Services
    :instance-property : (WebApplication -> System.IServiceProvider)])

;; --- Accessors ---

;; The builder's service collection — register services on this before app/build
;; (with the di-abstractions registration verbs).
(define (services/builder-services
          [builder : WebApplicationBuilder])
  : Microsoft.Extensions.DependencyInjection.IServiceCollection
  (builder-services builder))

;; The app's root service provider (singletons / startup-time resolution only).
(define (services/app-services [app : WebApplication])
  : System.IServiceProvider
  (app-services app))

(export services/builder-services services/app-services)
