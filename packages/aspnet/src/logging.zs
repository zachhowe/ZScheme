;; logging.zs — structured logging via Microsoft.Extensions.Logging's ILogger.
;;
;; Loggers are obtained from the DI container: ILoggerFactory is always registered
;; by the host, so logging/request-logger resolves it from the request-scoped
;; provider and asks for a category-named ILogger. The Log* verbs are extension
;; methods on ILogger in Microsoft.Extensions.Logging.Abstractions; each ends in a
;; `params object[] args`, bound here as an explicit (Mutable-Vector System.Object)
;; and fed by a variadic wrapper (mirroring stdlib/string's `format`). This supports
;; message-template logging, e.g. (log/info logger "user {Id} hit {Path}" id path).
(module logging)

(import aspnet/request)
(import aspnet/services)
;; Pull in the Mutable-Vector alias so the variadic log wrappers can pack their
;; rest arguments into the object[] the Log* extension methods expect.
(import stdlib/mutable/vector)

(import-clr
  Microsoft.AspNetCore.Builder
  Microsoft.Extensions.Logging

  ;; WebApplicationBuilder.Logging : ILoggingBuilder — configure providers pre-Build.
  [builder-logging Microsoft.AspNetCore.Builder.WebApplicationBuilder.Logging
    :instance-property : (Microsoft.AspNetCore.Builder.WebApplicationBuilder
                          -> Microsoft.Extensions.Logging.ILoggingBuilder)]
  ;; ClearProviders lives in Microsoft.Extensions.Logging.dll, NOT in the
  ;; Abstractions assembly the Log* verbs come from. Its namespace prefix is shared
  ;; by Microsoft.Extensions.Logging.Abstractions.dll, so FindType cannot reliably
  ;; probe it by name — load the assembly explicitly via :from.
  [clr-clear-providers Microsoft.Extensions.Logging.LoggingBuilderExtensions/ClearProviders
    :from "Microsoft.Extensions.Logging"
    : (Microsoft.Extensions.Logging.ILoggingBuilder
       -> Microsoft.Extensions.Logging.ILoggingBuilder)]

  ;; ILoggerFactory.CreateLogger(string) : ILogger — non-generic, category-named.
  [factory-create-logger Microsoft.Extensions.Logging.ILoggerFactory.CreateLogger
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance : (Microsoft.Extensions.Logging.ILoggerFactory String
                 -> Microsoft.Extensions.Logging.ILogger)]

  ;; LoggerExtensions.Log* — each (this ILogger, string message, params object[] args),
  ;; in Microsoft.Extensions.Logging.Abstractions.dll.
  [clr-log-trace Microsoft.Extensions.Logging.LoggerExtensions/LogTrace
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (Microsoft.Extensions.Logging.ILogger String (Mutable-Vector System.Object) -> Unit)]
  [clr-log-debug Microsoft.Extensions.Logging.LoggerExtensions/LogDebug
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (Microsoft.Extensions.Logging.ILogger String (Mutable-Vector System.Object) -> Unit)]
  [clr-log-info Microsoft.Extensions.Logging.LoggerExtensions/LogInformation
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (Microsoft.Extensions.Logging.ILogger String (Mutable-Vector System.Object) -> Unit)]
  [clr-log-warning Microsoft.Extensions.Logging.LoggerExtensions/LogWarning
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (Microsoft.Extensions.Logging.ILogger String (Mutable-Vector System.Object) -> Unit)]
  [clr-log-error Microsoft.Extensions.Logging.LoggerExtensions/LogError
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (Microsoft.Extensions.Logging.ILogger String (Mutable-Vector System.Object) -> Unit)]
  [clr-log-critical Microsoft.Extensions.Logging.LoggerExtensions/LogCritical
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (Microsoft.Extensions.Logging.ILogger String (Mutable-Vector System.Object) -> Unit)])

;; --- Provider configuration ---

;; Remove all logging providers from a builder (no log output). Use in tests or
;; quiet apps; app/create-builder leaves the framework defaults in place.
(define (logging/clear-providers
          [builder : Microsoft.AspNetCore.Builder.WebApplicationBuilder])
  : Microsoft.AspNetCore.Builder.WebApplicationBuilder
  (begin
    (clr-clear-providers (builder-logging builder))
    builder))

;; --- Logger acquisition ---

;; A category-named ILogger for the current request (resolves ILoggerFactory from
;; the request-scoped provider).
(define (logging/request-logger [ctx : Microsoft.AspNetCore.Http.HttpContext]
                                [category : String])
  : Microsoft.Extensions.Logging.ILogger
  (let ([factory : Microsoft.Extensions.Logging.ILoggerFactory
          (services/get-required-service (request/services ctx))])
    (factory-create-logger factory category)))

;; A category-named ILogger from the app's root provider (startup-time logging).
(define (logging/app-logger [app : Microsoft.AspNetCore.Builder.WebApplication]
                            [category : String])
  : Microsoft.Extensions.Logging.ILogger
  (let ([factory : Microsoft.Extensions.Logging.ILoggerFactory
          (services/get-required-service (services/app-services app))])
    (factory-create-logger factory category)))

;; --- Log verbs (message template + optional structured args) ---

(define (log/trace [logger : Microsoft.Extensions.Logging.ILogger] [msg : String]
                   [args : System.Object ...]) : Unit
  (clr-log-trace logger msg args))

(define (log/debug [logger : Microsoft.Extensions.Logging.ILogger] [msg : String]
                   [args : System.Object ...]) : Unit
  (clr-log-debug logger msg args))

(define (log/info [logger : Microsoft.Extensions.Logging.ILogger] [msg : String]
                  [args : System.Object ...]) : Unit
  (clr-log-info logger msg args))

(define (log/warning [logger : Microsoft.Extensions.Logging.ILogger] [msg : String]
                     [args : System.Object ...]) : Unit
  (clr-log-warning logger msg args))

(define (log/error [logger : Microsoft.Extensions.Logging.ILogger] [msg : String]
                   [args : System.Object ...]) : Unit
  (clr-log-error logger msg args))

(define (log/critical [logger : Microsoft.Extensions.Logging.ILogger] [msg : String]
                      [args : System.Object ...]) : Unit
  (clr-log-critical logger msg args))

(export logging/clear-providers
        logging/request-logger logging/app-logger
        log/trace log/debug log/info log/warning log/error log/critical)
