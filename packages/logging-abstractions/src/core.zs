;; core.zs — structured logging over Microsoft.Extensions.Logging.Abstractions.
;;
;; This module wraps the stable, provider-agnostic surface of the Abstractions
;; assembly: obtaining a category-named ILogger from an ILoggerFactory, the
;; LogLevel enum values, the IsEnabled guard, and the LoggerExtensions Log* verbs.
;; Each verb ends in a `params object[] args`, bound here as an explicit
;; (Mutable-Vector System.Object) and fed by a variadic wrapper (mirroring
;; stdlib/string's `format`), so message-template logging works:
;;   (log/info logger "user {Id} hit {Path}" id path)
;; Code that creates a concrete factory or configures providers lives in the
;; separate `logging` package, which depends on this one.
(module core)

;; Pull in the Mutable-Vector alias so the variadic log wrappers can pack their
;; rest arguments into the object[] the Log* extension methods expect.
(import stdlib/mutable/vector)

(import-clr
  Microsoft.Extensions.Logging

  ;; ILoggerFactory.CreateLogger(string) : ILogger — non-generic, category-named.
  [factory-create-logger Microsoft.Extensions.Logging.ILoggerFactory.CreateLogger
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance : (Microsoft.Extensions.Logging.ILoggerFactory String
                 -> Microsoft.Extensions.Logging.ILogger)]

  ;; ILogger.IsEnabled(LogLevel) : bool — guard before building an expensive message.
  [logger-is-enabled Microsoft.Extensions.Logging.ILogger.IsEnabled
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance : (Microsoft.Extensions.Logging.ILogger
                 Microsoft.Extensions.Logging.LogLevel -> Bool)]

  ;; LogLevel enum members. These are static literal fields; the codegen static-field
  ;; fallback emits them as their integer constant (cf. CancellationToken/None in
  ;; stdlib/concurrent/cancellation.zs binding a static get-only member as (-> T)).
  [clr-log-level-trace Microsoft.Extensions.Logging.LogLevel/Trace
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> Microsoft.Extensions.Logging.LogLevel)]
  [clr-log-level-debug Microsoft.Extensions.Logging.LogLevel/Debug
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> Microsoft.Extensions.Logging.LogLevel)]
  [clr-log-level-information Microsoft.Extensions.Logging.LogLevel/Information
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> Microsoft.Extensions.Logging.LogLevel)]
  [clr-log-level-warning Microsoft.Extensions.Logging.LogLevel/Warning
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> Microsoft.Extensions.Logging.LogLevel)]
  [clr-log-level-error Microsoft.Extensions.Logging.LogLevel/Error
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> Microsoft.Extensions.Logging.LogLevel)]
  [clr-log-level-critical Microsoft.Extensions.Logging.LogLevel/Critical
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> Microsoft.Extensions.Logging.LogLevel)]
  [clr-log-level-none Microsoft.Extensions.Logging.LogLevel/None
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> Microsoft.Extensions.Logging.LogLevel)]

  ;; NullLogger.Instance : a no-op ILogger singleton — useful as a default/sink.
  [clr-null-logger Microsoft.Extensions.Logging.Abstractions.NullLogger/Instance
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> Microsoft.Extensions.Logging.ILogger)]

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

;; --- LogLevel values ---

(define log-level/trace (clr-log-level-trace))
(define log-level/debug (clr-log-level-debug))
(define log-level/information (clr-log-level-information))
(define log-level/warning (clr-log-level-warning))
(define log-level/error (clr-log-level-error))
(define log-level/critical (clr-log-level-critical))
(define log-level/none (clr-log-level-none))

;; --- Logger acquisition ---

;; A category-named ILogger from a factory. The factory itself is produced either by
;; the host (e.g. aspnet resolves ILoggerFactory from DI) or by logging/create-factory.
(define (logger/from-factory [factory : Microsoft.Extensions.Logging.ILoggerFactory]
                             [category : String])
  : Microsoft.Extensions.Logging.ILogger
  (factory-create-logger factory category))

;; A shared no-op logger that discards everything written to it.
(define null-logger (clr-null-logger))

;; True when `logger` would actually emit at `level` (skip building costly messages).
(define (logger/enabled? [logger : Microsoft.Extensions.Logging.ILogger]
                         [level : Microsoft.Extensions.Logging.LogLevel]) : Bool
  (logger-is-enabled logger level))

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

(export logger/from-factory null-logger logger/enabled?
        log-level/trace log-level/debug log-level/information log-level/warning
        log-level/error log-level/critical log-level/none
        log/trace log/debug log/info log/warning log/error log/critical)
