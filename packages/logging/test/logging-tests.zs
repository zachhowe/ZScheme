;; logging-tests.zs — exercises standalone factory creation and builder config.
;;
;; Builds a real ILoggerFactory via logging/create-factory, running the configure
;; callback (clear-providers + set-minimum-level) against the live ILoggingBuilder,
;; then turns it into a category logger with logging-abstractions' logger/from-factory
;; and logs through it. With all providers cleared the logger has no sinks, so every
;; level reports disabled — a deterministic assertion that still drives each binding.
(namespace ZScheme.Logging.Tests)
(module logging-tests)

(import zunit)
(import logging/builder)
(import logging-abstractions/core)
(import-clr Microsoft.Extensions.Logging)

;; Configure callback: silence output and raise the floor to Warning.
(define (configure [b : ILoggingBuilder]) : Unit
  (begin
    (logging-builder/clear-providers b)
    (logging-builder/set-minimum-level b log-level/warning)
    ()))

(test-suite LoggingBuilderTests
  (test-case create_factory_and_log
    (let* ([factory (logging/create-factory configure)]
           [logger (logger/from-factory factory "Test.Builder")])
      (log/info logger "below the floor {N}" 1)
      (log/warning logger "at the floor {Name}" "x")
      (log/error logger "above the floor")
      ;; No providers were registered, so nothing is enabled.
      (check-false (logger/enabled? logger log-level/warning)))))
