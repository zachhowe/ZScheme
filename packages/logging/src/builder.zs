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
  [clr-create-factory LoggerFactory/Create
    :from "Microsoft.Extensions.Logging"
    :static : ((ILoggingBuilder -> Unit) -> ILoggerFactory)]

  ;; LoggingBuilderExtensions.ClearProviders / SetMinimumLevel live in
  ;; Microsoft.Extensions.Logging.dll (NOT Abstractions); load it explicitly via :from
  ;; since the shared namespace prefix makes name-probing ambiguous.
  [clr-clear-providers LoggingBuilderExtensions/ClearProviders
    :from "Microsoft.Extensions.Logging"
    : (ILoggingBuilder -> ILoggingBuilder)]
  [clr-set-minimum-level LoggingBuilderExtensions/SetMinimumLevel
    :from "Microsoft.Extensions.Logging"
    : (ILoggingBuilder LogLevel -> ILoggingBuilder)])

;; --- Factory creation ---

;; Build a standalone ILoggerFactory; `configure` receives the ILoggingBuilder to add
;; providers / set levels on (e.g. (lambda (b) (logging-builder/set-minimum-level b lvl))).
(define (logging/create-factory
          [configure : (ILoggingBuilder -> Unit)])
  : ILoggerFactory
  (clr-create-factory configure))

;; --- Builder configuration (return the builder for chaining inside `configure`) ---

;; Remove all registered logging providers (no output).
(define (logging-builder/clear-providers
          [builder : ILoggingBuilder])
  : ILoggingBuilder
  (clr-clear-providers builder))

;; Set the minimum level below which messages are dropped.
(define (logging-builder/set-minimum-level
          [builder : ILoggingBuilder]
          [level : LogLevel])
  : ILoggingBuilder
  (clr-set-minimum-level builder level))

(export logging/create-factory
        logging-builder/clear-providers logging-builder/set-minimum-level)
