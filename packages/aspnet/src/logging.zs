;; logging.zs — ASP.NET-specific logging glue over the standalone logging packages.
;;
;; The provider-agnostic surface (acquiring an ILogger from a factory, the log/* verbs,
;; LogLevel) lives in the `logging-abstractions` package, and ILoggingBuilder
;; configuration lives in the `logging` package. This module only adds the bindings
;; that genuinely need ASP.NET types: resolving an ILoggerFactory from a request or app
;; provider, and clearing providers on a WebApplicationBuilder. Import the log/* verbs
;; directly from `logging-abstractions/core` at the call site.
(module logging)

(import aspnet/request)
(import aspnet/services)
(import di-abstractions/services)
(import logging-abstractions/core)
(import logging/builder)

(import-clr
  Microsoft.AspNetCore.Builder

  ;; WebApplicationBuilder.Logging : ILoggingBuilder — configure providers pre-Build.
  [builder-logging Microsoft.AspNetCore.Builder.WebApplicationBuilder.Logging
    :instance-property : (Microsoft.AspNetCore.Builder.WebApplicationBuilder
                          -> Microsoft.Extensions.Logging.ILoggingBuilder)])

;; --- Provider configuration ---

;; Remove all logging providers from a builder (no log output). Use in tests or
;; quiet apps; app/create-builder leaves the framework defaults in place.
(define (logging/clear-providers
          [builder : Microsoft.AspNetCore.Builder.WebApplicationBuilder])
  : Microsoft.AspNetCore.Builder.WebApplicationBuilder
  (begin
    (logging-builder/clear-providers (builder-logging builder))
    builder))

;; --- Logger acquisition ---

;; A category-named ILogger for the current request (resolves ILoggerFactory from
;; the request-scoped provider).
(define (logging/request-logger [ctx : Microsoft.AspNetCore.Http.HttpContext]
                                [category : String])
  : Microsoft.Extensions.Logging.ILogger
  (let ([factory : Microsoft.Extensions.Logging.ILoggerFactory
          (services/get-required-service (request/services ctx))])
    (logger/from-factory factory category)))

;; A category-named ILogger from the app's root provider (startup-time logging).
(define (logging/app-logger [app : Microsoft.AspNetCore.Builder.WebApplication]
                            [category : String])
  : Microsoft.Extensions.Logging.ILogger
  (let ([factory : Microsoft.Extensions.Logging.ILoggerFactory
          (services/get-required-service (services/app-services app))])
    (logger/from-factory factory category)))

(export logging/clear-providers
        logging/request-logger logging/app-logger)
