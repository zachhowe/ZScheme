;; builder.zs — logger-factory creation and provider configuration over
;; Microsoft.Extensions.Logging (the concrete layer above the Abstractions package).
;;
;; `logging/create-factory` builds a standalone ILoggerFactory from a configure
;; callback; the logging-builder/* verbs run inside that callback (or any host's
;; ILoggingBuilder, e.g. aspnet's WebApplicationBuilder.Logging) to clear providers
;; or raise the minimum level. Turn the resulting factory into a category logger with
;; logging-abstractions' `logger/from-factory`.
(module builder)

(import-clr
  Microsoft.Extensions.Logging

  ;; LoggerFactory.Create(Action<ILoggingBuilder>) : ILoggerFactory — a standalone
  ;; factory configured by the callback. The ZScheme (ILoggingBuilder -> Unit) lambda
  ;; coerces to the Action<ILoggingBuilder> parameter.
  [clr-create-factory Microsoft.Extensions.Logging.LoggerFactory/Create
    :from "Microsoft.Extensions.Logging"
    :static : ((Microsoft.Extensions.Logging.ILoggingBuilder -> Unit)
               -> Microsoft.Extensions.Logging.ILoggerFactory)]

  ;; LoggingBuilderExtensions.ClearProviders / SetMinimumLevel live in
  ;; Microsoft.Extensions.Logging.dll (NOT Abstractions); load it explicitly via :from
  ;; since the shared namespace prefix makes name-probing ambiguous.
  [clr-clear-providers Microsoft.Extensions.Logging.LoggingBuilderExtensions/ClearProviders
    :from "Microsoft.Extensions.Logging"
    : (Microsoft.Extensions.Logging.ILoggingBuilder
       -> Microsoft.Extensions.Logging.ILoggingBuilder)]
  [clr-set-minimum-level Microsoft.Extensions.Logging.LoggingBuilderExtensions/SetMinimumLevel
    :from "Microsoft.Extensions.Logging"
    : (Microsoft.Extensions.Logging.ILoggingBuilder
       Microsoft.Extensions.Logging.LogLevel
       -> Microsoft.Extensions.Logging.ILoggingBuilder)])

;; --- Factory creation ---

;; Build a standalone ILoggerFactory; `configure` receives the ILoggingBuilder to add
;; providers / set levels on (e.g. (lambda (b) (logging-builder/set-minimum-level b lvl))).
(define (logging/create-factory
          [configure : (Microsoft.Extensions.Logging.ILoggingBuilder -> Unit)])
  : Microsoft.Extensions.Logging.ILoggerFactory
  (clr-create-factory configure))

;; --- Builder configuration (return the builder for chaining inside `configure`) ---

;; Remove all registered logging providers (no output).
(define (logging-builder/clear-providers
          [builder : Microsoft.Extensions.Logging.ILoggingBuilder])
  : Microsoft.Extensions.Logging.ILoggingBuilder
  (clr-clear-providers builder))

;; Set the minimum level below which messages are dropped.
(define (logging-builder/set-minimum-level
          [builder : Microsoft.Extensions.Logging.ILoggingBuilder]
          [level : Microsoft.Extensions.Logging.LogLevel])
  : Microsoft.Extensions.Logging.ILoggingBuilder
  (clr-set-minimum-level builder level))

(export logging/create-factory
        logging-builder/clear-providers logging-builder/set-minimum-level)
