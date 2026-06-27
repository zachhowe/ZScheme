;; logging-abstractions-tests.zs — exercises the provider-agnostic logging surface.
;;
;; Uses the no-op NullLogger so the bindings run without any configured provider:
;; every Log* verb (plain, templated, multi-arg, no-arg), the LogLevel constants, and
;; the IsEnabled guard are invoked. NullLogger.IsEnabled is always false, which lets us
;; assert a concrete result while still driving each binding end-to-end.
(namespace ZScheme.Logging.Abstractions.Tests)
(module logging-abstractions-tests)

(import zunit)
(import logging-abstractions/core)

(test-suite LoggingAbstractionsTests
  ;; NullLogger reports every level disabled.
  (test-case null_logger_disabled_at_all_levels
    (check-false (logger/enabled? null-logger log-level/trace))
    (check-false (logger/enabled? null-logger log-level/debug))
    (check-false (logger/enabled? null-logger log-level/information))
    (check-false (logger/enabled? null-logger log-level/warning))
    (check-false (logger/enabled? null-logger log-level/error))
    (check-false (logger/enabled? null-logger log-level/critical)))

  ;; Every verb runs over a real ILogger without error (output is discarded).
  (test-case verbs_run_against_null_logger
    (log/trace null-logger "trace {A}" 1)
    (log/debug null-logger "debug")
    (log/info null-logger "user {Id} hit {Path}" 42 "/x")
    (log/warning null-logger "slow {Name} took {Ms}ms" "lookup" 12)
    (log/error null-logger "no-arg error message")
    (log/critical null-logger "boom {Code}" 500)
    ;; A trailing assertion gives the case a concrete expectation.
    (check-false (logger/enabled? null-logger log-level/none))))
