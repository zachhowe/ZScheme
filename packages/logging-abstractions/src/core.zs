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

(import-clr
  Microsoft.Extensions.Logging

  ;; ILoggerFactory.CreateLogger(string) : ILogger — non-generic, category-named.
  [factory-create-logger ILoggerFactory.CreateLogger
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance : (ILoggerFactory String -> ILogger)]

  ;; ILogger.IsEnabled(LogLevel) : bool — guard before building an expensive message.
  [logger-is-enabled ILogger.IsEnabled
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance : (ILogger LogLevel -> Bool)]

  ;; LogLevel enum members. These are static literal fields; the codegen static-field
  ;; fallback emits them as their integer constant (cf. CancellationToken/None in
  ;; stdlib/concurrent/cancellation.zs binding a static get-only member as (-> T)).
  [clr-log-level-trace LogLevel/Trace
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> LogLevel)]
  [clr-log-level-debug LogLevel/Debug
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> LogLevel)]
  [clr-log-level-information LogLevel/Information
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> LogLevel)]
  [clr-log-level-warning LogLevel/Warning
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> LogLevel)]
  [clr-log-level-error LogLevel/Error
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> LogLevel)]
  [clr-log-level-critical LogLevel/Critical
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> LogLevel)]
  [clr-log-level-none LogLevel/None
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> LogLevel)]

  ;; NullLogger.Instance : a no-op ILogger singleton — useful as a default/sink.
  [clr-null-logger Microsoft.Extensions.Logging.Abstractions.NullLogger/Instance
    :from "Microsoft.Extensions.Logging.Abstractions"
    :instance-property : (-> ILogger)]

  ;; LoggerExtensions.Log* — each (this ILogger, string message, params object[] args),
  ;; in Microsoft.Extensions.Logging.Abstractions.dll.
  [clr-log-trace LoggerExtensions/LogTrace
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (ILogger String (Mutable-Vector System.Object) -> Unit)]
  [clr-log-debug LoggerExtensions/LogDebug
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (ILogger String (Mutable-Vector System.Object) -> Unit)]
  [clr-log-info LoggerExtensions/LogInformation
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (ILogger String (Mutable-Vector System.Object) -> Unit)]
  [clr-log-warning LoggerExtensions/LogWarning
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (ILogger String (Mutable-Vector System.Object) -> Unit)]
  [clr-log-error LoggerExtensions/LogError
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (ILogger String (Mutable-Vector System.Object) -> Unit)]
  [clr-log-critical LoggerExtensions/LogCritical
    :from "Microsoft.Extensions.Logging.Abstractions"
    : (ILogger String (Mutable-Vector System.Object) -> Unit)])

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
(define (logger/from-factory [factory : ILoggerFactory]
                             [category : String])
  : ILogger
  (factory-create-logger factory category))

;; A shared no-op logger that discards everything written to it.
(define null-logger (clr-null-logger))

;; True when `logger` would actually emit at `level` (skip building costly messages).
(define (logger/enabled? [logger : ILogger]
                         [level : LogLevel]) : Bool
  (logger-is-enabled logger level))

;; --- Log verbs (message template + optional structured args) ---

(define (log/trace [logger : ILogger] [msg : String]
                   [args : System.Object ...]) : Unit
  (clr-log-trace logger msg args))

(define (log/debug [logger : ILogger] [msg : String]
                   [args : System.Object ...]) : Unit
  (clr-log-debug logger msg args))

(define (log/info [logger : ILogger] [msg : String]
                  [args : System.Object ...]) : Unit
  (clr-log-info logger msg args))

(define (log/warning [logger : ILogger] [msg : String]
                     [args : System.Object ...]) : Unit
  (clr-log-warning logger msg args))

(define (log/error [logger : ILogger] [msg : String]
                   [args : System.Object ...]) : Unit
  (clr-log-error logger msg args))

(define (log/critical [logger : ILogger] [msg : String]
                      [args : System.Object ...]) : Unit
  (clr-log-critical logger msg args))

(export logger/from-factory null-logger logger/enabled?
        log-level/trace log-level/debug log-level/information log-level/warning
        log-level/error log-level/critical log-level/none
        log/trace log/debug log/info log/warning log/error log/critical)
